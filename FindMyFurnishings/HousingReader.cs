using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace FindMyFurnishings;

/// <summary>
/// Reads the property you are standing in.
///
/// The client only holds furnishing data for the zone it has loaded, so this is the only
/// moment the information exists. Everything the plugin can say about a property later is
/// whatever was captured here.
/// </summary>
public sealed unsafe class HousingReader
{
    // The property's containers, found by name rather than listed by hand.
    //
    // Each set is a run of pages the game numbers in its own enum - Storeroom1 to
    // Storeroom11 today. Writing those out fixed the plugin to today's count: a page added
    // by a later patch, which is how more storage would arrive, was simply never read, and
    // nothing about that looked like an error. Discovered this way, a new page is picked up
    // by rebuilding against the game's new data, with no change here.

    /// <summary>Interior storerooms, in page order. The exterior ones are separate.</summary>
    private static readonly InventoryType[] InteriorStore = Pages("HousingInteriorStoreroom");

    private static readonly InventoryType[] ExteriorStore = Pages("HousingExteriorStoreroom");

    /// <summary>
    /// Items placed in this property, as the property's own containers.
    ///
    /// NOT read from the furniture manager's table. That table is not scoped to your
    /// estate - standing in a yard it holds every plot rendered around you, which indexed
    /// the neighbours' gardens as if they were yours. These containers belong to the
    /// estate itself, and their sizes are the game's own placement limits.
    /// </summary>
    private static readonly InventoryType[] InteriorPlaced = Pages("HousingInteriorPlacedItems");

    private static readonly InventoryType[] ExteriorPlaced = Pages("HousingExteriorPlacedItems");

    /// <summary>
    /// Every container whose name is this prefix followed by a page number, in page order.
    /// The first page of some sets carries no number at all, and counts as page one.
    /// </summary>
    private static InventoryType[] Pages(string prefix)
    {
        var found = new List<(int Page, InventoryType Type)>();

        foreach (var name in Enum.GetNames<InventoryType>())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var tail = name[prefix.Length..];
            var page = 1;

            // Anything else after the prefix is a different container that merely starts
            // the same way, not a page of this one.
            if (tail.Length > 0 && !int.TryParse(tail, out page)) continue;

            found.Add((page, Enum.Parse<InventoryType>(name)));
        }

        found.Sort((x, y) => x.Page.CompareTo(y.Page));
        return found.ConvertAll(f => f.Type).ToArray();
    }

    private readonly FurnitureSheet sheet;
    private readonly IDataManager data;
    private readonly IPluginLog log;

    public HousingReader(FurnitureSheet sheet, IDataManager data, IPluginLog log)
    {
        this.sheet = sheet;
        this.data = data;
        this.log = log;
    }

    /// <summary>
    /// True when we are standing in a property whose contents we are entitled to record.
    ///
    /// Gated on the game's own permission check, so a friend's house you are merely
    /// visiting is never indexed. That is a deliberate limit, not an oversight: an index
    /// of somebody else's belongings is not this plugin's business.
    /// </summary>
    public static bool CanIndexHere() => CanIndexHere(false, out _);

    /// <summary>
    /// Whether this property may be recorded, and why.
    ///
    /// The teleport list is the authority when it answers: it is the game's own list of
    /// the estates you hold, and it names a shared estate as such so one can be refused
    /// outright. But it cannot always identify where you are standing - indoors the house
    /// id carries the interior territory, which no teleport destination matches - and
    /// making it the only test meant walking into your own house recorded nothing at all.
    ///
    /// So there are two fallbacks. An open Housing window is the stronger of them: the
    /// game only opens one for a property you are allowed to edit, which makes it an
    /// access check as well as the freshness guarantee it was already needed for. It is
    /// also the only one that works in a yard, where HasHousePermissions() is false even
    /// on your own plot.
    ///
    /// Failing that, the game's permission check, which does work indoors. Both fallbacks
    /// are looser than the teleport list: they are true for a Free Company estate you
    /// have been granted editing rights to, which is the point. An explicitly shared
    /// estate is refused whichever path is taken.
    /// </summary>
    public static bool CanIndexHere(bool housingWindowOpen, out string why,
                                    bool allowShared = false)
    {
        why = "not in a property";

        var mgr = HousingManager.Instance();
        if (mgr == null) return false;
        if (!mgr->IsInside() && !mgr->IsOutside()) return false;

        var estate = EstateOf(mgr->GetCurrentHouseId().Id, out var diagnosis);

        if (estate == EstateType.SharedEstate && !allowShared)
        {
            why = "a shared estate - only estates you own are recorded";
            return false;
        }

        if (estate != null)
        {
            why = $"{estate} (teleport list)";
            return true;
        }

        if (housingWindowOpen)
        {
            why = "the Housing window is open, which the game allows only where you can edit";
            return true;
        }

        if (mgr->HasHousePermissions())
        {
            why = $"estate editing rights ({diagnosis})";
            return true;
        }

        why = $"not identified as yours ({diagnosis})";
        return false;
    }

    /// <summary>
    /// The identity of the property underfoot, without reading any of its contents.
    ///
    /// Exists so the periodic check can ask "am I somewhere new?" cheaply. Capturing first
    /// and comparing afterwards did the full scan every few seconds and threw it away.
    /// </summary>
    public static string CurrentKeyId(bool housingWindowOpen = false)
    {
        var mgr = HousingManager.Instance();
        // Identity only, never contents, so a shared estate is allowed through here - the
        // capture path does its own gating and needs to know where you are standing in
        // order to apply it.
        if (mgr == null || !CanIndexHere(housingWindowOpen, out _, allowShared: true))
            return string.Empty;

        return KeyOf(mgr).Id;
    }

    /// <summary>
    /// How many of this property's containers are loaded right now.
    ///
    /// Placed items and storerooms only arrive once the housing or storeroom window has
    /// been opened, so this number rising is the signal that a property recorded as
    /// incomplete is worth recording again.
    /// </summary>
    public static int LoadedStoreroomPages(bool indoors)
    {
        var inv = InventoryManager.Instance();
        if (inv == null) return 0;

        var n = 0;
        foreach (var set in new[]
                 {
                     indoors ? InteriorPlaced : ExteriorPlaced,
                     indoors ? InteriorStore : ExteriorStore,
                 })
        {
            foreach (var t in set)
            {
                var c = inv->GetInventoryContainer(t);
                if (c != null && c->IsLoaded) n++;
            }
        }
        return n;
    }

    /// <summary>
    /// A cheap value that changes whenever the housing containers do.
    ///
    /// Walks the same pages a capture would, but touches only the raw slot ids - no sheet
    /// lookups, no allocation, no snapshot. That is what makes it safe to run several
    /// times a second while a Housing window is open, so a piece moved is noticed at once
    /// rather than whenever the next slow tick happens to land.
    ///
    /// Not loaded is folded in deliberately: pages arriving or going away is itself a
    /// change worth re-reading for.
    /// </summary>
    public static long RawSignature(bool indoors)
    {
        var inv = InventoryManager.Instance();
        if (inv == null) return 0;

        long sig = 17;

        foreach (var set in new[]
                 {
                     indoors ? InteriorPlaced : ExteriorPlaced,
                     indoors ? InteriorStore : ExteriorStore,
                 })
        {
            foreach (var t in set)
            {
                var c = inv->GetInventoryContainer(t);
                if (c == null || !c->IsLoaded)
                {
                    sig = (sig * 31) + 1;
                    continue;
                }

                sig = (sig * 31) + c->Size + 2;

                for (var slot = 0; slot < c->Size; slot++)
                {
                    var item = c->GetInventorySlot(slot);
                    if (item == null || item->ItemId == 0) continue;

                    // Slot index included, so moving a piece from one page to another is a
                    // change even when the set of ids is identical.
                    sig += ((long)item->ItemId * 31) + ((long)slot << 17);
                }
            }
        }

        return sig;
    }

    /// <summary>Refuses to walk a list longer than this. A bad pointer should not hang the game.</summary>
    private const int MaxTeleportEntries = 512;

    /// <summary>Ward index, bits 0-5 of the packed house id's Data2 field.</summary>
    private static ushort WardOf(HouseId id) => (ushort)(id.Data2 & 0x3F);

    private static bool Filled(TeleportInfo* e) =>
        e->HouseId.Id != ulong.MaxValue && (byte)e->EstateType != 0xFF;

    /// <summary>
    /// What kind of estate this is, according to the game itself.
    ///
    /// The teleport menu distinguishes Estate Hall (Private), Estate Hall (Free Company),
    /// Shared Estate and Apartment, and this is where that comes from: each destination
    /// carries an EstateType and the HouseId it belongs to. Nothing on HousingManager
    /// makes that distinction.
    ///
    /// Returns null whenever the list is missing, empty or unfilled, in which case naming
    /// falls back to what the address alone can settle. That is the common case rather
    /// than the exception, so it has to be cheap and silent.
    /// </summary>
    public static EstateType? EstateHere() => EstateOf(CurrentHouseIdOrZero(), out _);

    private static ulong CurrentHouseIdOrZero()
    {
        var mgr = HousingManager.Instance();
        return mgr == null ? 0UL : mgr->GetCurrentHouseId().Id;
    }

    /// <summary>
    /// Shared by detection and by /fmf dump, so the diagnostics describe the same read the
    /// plugin actually performs rather than a parallel one that might differ.
    /// </summary>
    private static ulong estateCacheId;
    private static short estateCacheRoom;
    private static EstateType? estateCacheValue;
    private static string estateCacheDiagnosis = string.Empty;
    private static DateTime estateCacheAt = DateTime.MinValue;

    /// <summary>
    /// Which kind of estate a house id is, remembered for a few seconds.
    ///
    /// The uncached answer comes from a GAME function, and calling that function at the
    /// wrong moment crashed the client outright. A property does not change what it is, so
    /// asking once and keeping the answer removes almost every call without costing
    /// anything: the only thing that moves is which property you are standing in, and that
    /// is a different id.
    /// </summary>
    public static EstateType? EstateOf(ulong houseId, out string diagnosis)
    {
        // Nowhere, so nothing to ask about - and asked without the cache every time, since
        // a zero id was never remembered. That was the game function being called from
        // places with no property under them at all.
        if (houseId == 0)
        {
            diagnosis = "not in a property";
            return null;
        }

        // The room is part of the question. Whether an unmatched property is a chamber
        // depends on standing in one, so an answer worked out in the hall must not be
        // handed back a moment later in a room off it.
        var room = CurrentRoomOrZero();

        if (houseId == estateCacheId && room == estateCacheRoom
            && DateTime.UtcNow - estateCacheAt < TimeSpan.FromSeconds(10))
        {
            diagnosis = estateCacheDiagnosis;
            return estateCacheValue;
        }

        var answer = EstateOfUncached(houseId, room, out diagnosis);

        estateCacheId = houseId;
        estateCacheRoom = room;
        estateCacheValue = answer;
        estateCacheDiagnosis = diagnosis;
        estateCacheAt = DateTime.UtcNow;

        return answer;
    }

    private static short CurrentRoomOrZero()
    {
        var mgr = HousingManager.Instance();
        return mgr == null ? (short)0 : mgr->GetCurrentRoom();
    }

    private static EstateType? EstateOfUncached(ulong houseId, short room, out string diagnosis)
    {
        diagnosis = "no telepo";

        try
        {
            var telepo = Telepo.Instance();
            if (telepo == null) return null;

            // The vector this hands back, not the field: reading the field directly gave
            // a list of entries that were uniformly unset.
            var list = telepo->UpdateAetheryteList();
            if (list == null) { diagnosis = "update returned null"; return null; }

            var first = list->First;
            var last = list->Last;

            if (first == null || last == null || last < first)
            {
                diagnosis = "bad list bounds";
                return null;
            }

            var count = (int)(last - first);
            if (count <= 0) { diagnosis = "list empty"; return null; }
            if (count > MaxTeleportEntries) { diagnosis = $"list implausibly long ({count})"; return null; }

            var filled = 0;
            for (var i = 0; i < count; i++)
                if (Filled(first + i)) filled++;

            diagnosis = $"{count} entries, {filled} filled";
            if (filled == 0) return null;

            var here = new HouseId { Id = houseId };

            for (var i = 0; i < count; i++)
            {
                var e = first + i;
                if (!Filled(e)) continue;
                if (e->HouseId.Id == houseId) { diagnosis += ", exact match"; return e->EstateType; }
            }

            // Indoors the id carries the INTERIOR's territory, which no teleport entry
            // has, so the hall of an estate you own never matches exactly. Compared again
            // with the territory left out. Only a single match counts: the same ward and
            // plot numbers exist in every district, and two entries agreeing on everything
            // but the district cannot say which one this is.
            var territoryBits = new HouseId { TerritoryTypeId = ushort.MaxValue }.Id;
            EstateType? loose = null;
            var looseMatches = 0;

            for (var i = 0; i < count; i++)
            {
                var e = first + i;
                if (!Filled(e)) continue;
                if ((e->HouseId.Id & ~territoryBits) != (houseId & ~territoryBits)) continue;

                loose = e->EstateType;
                looseMatches++;
            }

            if (looseMatches == 1)
            {
                diagnosis += ", match ignoring territory";
                return loose;
            }

            // A chamber is a room. This used to call ANY unmatched property in the same
            // ward as an estate of yours a chamber - which named the hall itself, and an
            // apartment in that ward, "Free Company Chamber" and read only 150 of their
            // slots. Standing in a numbered room that is not an apartment is what makes it
            // one; anything else unmatched is simply not identified.
            if (room <= 0 || here.IsApartment)
            {
                diagnosis += ", no entry for this property and not in a chamber";
                return null;
            }

            // A Free Company chamber has no teleport entry of its own - you reach it
            // through the estate. So an unmatched room in the same world and ward as
            // exactly one estate you own is a chamber inside it. Shared estates are left
            // out of that count: chambers do not exist in them, and counting them would
            // make a guest's ward look ambiguous.
            var candidates = 0;

            for (var i = 0; i < count; i++)
            {
                var e = first + i;
                if (!Filled(e)) continue;
                if (e->EstateType == EstateType.SharedEstate) continue;
                if (e->HouseId.WorldId != here.WorldId) continue;
                if (WardOf(e->HouseId) != WardOf(here)) continue;

                candidates++;
            }

            diagnosis += $", {candidates} owned estate(s) in this ward";
            if (candidates == 1) return EstateType.PersonalChambers;
        }
        catch (Exception ex)
        {
            diagnosis = $"threw: {ex.Message}";
        }

        return null;
    }

    /// <summary>
    /// True when this estate is one the player is a guest on rather than an owner of.
    ///
    /// Shared estates are deliberately never indexed. Access is not ownership, and a
    /// record of what is in somebody else's house is not this plugin's to keep.
    /// </summary>
    public static bool IsSharedEstate() => EstateHere() == EstateType.SharedEstate;

    public static bool IsIndoors()
    {
        var mgr = HousingManager.Instance();
        return mgr != null && mgr->IsInside();
    }

    public PropertySnapshot? Capture(bool housingWindowOpen, bool allowShared = false)
    {
        var mgr = HousingManager.Instance();
        if (mgr == null || !CanIndexHere(housingWindowOpen, out _, allowShared)) return null;
        if (!allowShared && IsSharedEstate()) return null;

        var indoors = mgr->IsInside();
        var key = KeyOf(mgr);

        var snap = new PropertySnapshot
        {
            Key = key,
            Outdoors = !indoors,
            TakenUtc = DateTime.UtcNow,

            // Taken here, at the one moment it is certainly right: the character standing
            // in the room is the character whose room it is.
            Owner = Plugin.LocalPlayerName,
            FreeCompany = FreeCompanyName(),
        };
        Describe(snap, probeGame: true);

        // Only as many pages as this property can fill. The containers are shared across
        // an estate and its chambers, and the pages past a chamber's allocation still hold
        // the hall's storeroom - reading all eleven gave 186 stored where the game showed
        // 91. The placed pages beyond the allocation came back empty, which is why placed
        // was right all along and only storage was inflated.
        var pages = PagesFor(snap.Estate);

        snap.PlacedLoaded = ReadContainers(indoors ? InteriorPlaced : ExteriorPlaced,
                                           indoors ? Spot.IndoorPlaced : Spot.OutdoorPlaced,
                                           snap, pages, out var placedSlots);
        snap.StoreroomLoaded = ReadContainers(indoors ? InteriorStore : ExteriorStore,
                                              indoors ? Spot.IndoorStoreroom
                                                      : Spot.OutdoorStoreroom, snap, pages,
                                              out var storedSlots);

        // Only when the pages actually answered. A capacity taken from containers that had
        // not loaded would read as zero room, which is worse than saying nothing.
        if (snap.PlacedLoaded) snap.PlacedCapacity = placedSlots;
        if (snap.StoreroomLoaded) snap.StoredCapacity = storedSlots;

        snap.Partial = !snap.PlacedLoaded || !snap.StoreroomLoaded;
        snap.StoreroomPagesLoaded = LoadedStoreroomPages(indoors);

        log.Information($"[FMF] Container capacity at {snap.Address}: " +
                        $"{snap.PlacedCapacity} placed slots, {snap.StoredCapacity} stored slots " +
                        $"(estate={snap.Estate?.ToString() ?? "unknown"}).");

        log.Information($"[FMF] Captured {snap.Pieces.Count:N0} pieces at {snap.Address} " +
                        $"(placed loaded: {snap.PlacedLoaded}, storeroom loaded: " +
                        $"{snap.StoreroomLoaded}).");
        return snap;
    }

    /// <summary>
    /// Reads a set of the property's containers.
    ///
    /// Each page loads independently, exactly like the Free Company chest - asking "is it
    /// open" and then reading page 7 can report a confident zero for a page that simply
    /// has not arrived. So pages are checked one at a time, and any page that is missing
    /// marks the whole snapshot partial rather than quietly shrinking the count.
    /// </summary>
    /// <summary>Slots per housing container page.</summary>
    private const int PageSize = 50;

    /// <summary>
    /// How many pages of a property's containers are actually its own.
    ///
    /// A chamber holds 150, which is three pages; the rest of the estate's eleven belong
    /// to the hall. Properties with no smaller limit read everything.
    /// </summary>
    private static int PagesFor(EstateType? estate) => estate switch
    {
        EstateType.PersonalChambers => 150 / PageSize,
        EstateType.ApartmentRoom or EstateType.ApartmentBuilding => 150 / PageSize,
        _ => int.MaxValue,
    };

    /// <returns>True if at least one of these containers had actually loaded.</returns>
    /// <param name="slots">
    /// Total room across the pages that answered - the property's own capacity, straight
    /// from the game rather than from a table of estate sizes that would need maintaining.
    /// </param>
    private bool ReadContainers(InventoryType[] pages, Spot spot, PropertySnapshot snap,
                                int maxPages, out int slots)
    {
        slots = 0;

        var inv = InventoryManager.Instance();
        if (inv == null) return false;

        var any = false;
        var read = 0;

        foreach (var page in pages)
        {
            if (read >= maxPages) break;
            read++;

            var c = inv->GetInventoryContainer(page);
            // An unused page simply never loads; only whether ANY page answered matters.
            if (c == null || !c->IsLoaded) continue;

            any = true;
            slots += c->Size;

            for (var slot = 0; slot < c->Size; slot++)
            {
                var item = c->GetInventorySlot(slot);
                if (item == null || item->ItemId == 0) continue;
                if (!sheet.IsFurnishing(item->ItemId)) continue;

                snap.Pieces.Add(sheet.Make(item->ItemId, spot));
            }
        }

        return any;
    }

    private static PropertyKey KeyOf(HousingManager* mgr)
    {
        var id = mgr->GetCurrentHouseId();
        return new PropertyKey
        {
            WorldId = id.WorldId,
            TerritoryTypeId = id.TerritoryTypeId,
            Ward = mgr->GetCurrentWard(),
            Plot = mgr->GetCurrentPlot(),
            Division = mgr->GetCurrentDivision(),
            Room = mgr->GetCurrentRoom(),
            IsApartment = id.IsApartment,
        };
    }

    /// <summary>
    /// Names a property and files it under a section.
    ///
    /// Public and re-runnable, so entries recorded under older naming rules can be
    /// relabelled without having to go and stand in each one again.
    ///
    /// What the game will tell us is whether somewhere is an apartment, and whether it has
    /// a room number. What it will NOT tell us - at least through anything verified here -
    /// is whether a plot is a personal estate or a Free Company house. Plots therefore
    /// default to personal, and the section is left editable rather than guessed at.
    /// </summary>
    public void Describe(PropertySnapshot snap, bool probeGame = false)
    {
        var k = snap.Key;
        snap.Address = Address(k) + (snap.Outdoors ? " (yard)" : string.Empty);

        // Only the property underfoot may be probed. Relabelling stored entries must rely
        // on what was already resolved, or every property would inherit the estate type of
        // wherever you happen to be standing.
        var estate = snap.Estate ?? (probeGame ? EstateHere() : null);
        if (estate != null) snap.Estate = estate;

        switch (estate)
        {
            case EstateType.PersonalEstate:
                // "Owned" is said outright. A guest estate is a private estate too, just
                // not yours, and in a flat list the word is what tells the two apart.
                snap.DisplayName = snap.Outdoors
                    ? "Private Estate Yard (Owned)"
                    : Tagged("Private Estate", "Owned", SizeOf(k.TerritoryTypeId));
                if (!snap.SectionPinned) snap.Section = Section.PrivateEstate;
                return;

            case EstateType.FreeCompanyEstate:
                // No company name. The size and the address underneath say which house
                // it is; the name of the company is nobody's business who sees the list.
                // The game's own words. "Free Company" is not repeated on it: it sits
                // under a Free Company heading wherever it is listed.
                snap.DisplayName = snap.Outdoors
                    ? "Estate Yard"
                    : Tagged("Estate Hall", SizeOf(k.TerritoryTypeId));
                if (!snap.SectionPinned) snap.Section = Section.FreeCompany;
                return;

            case EstateType.PersonalChambers:
                // What the game calls them, and no name: they sit in their owner's section.
                snap.DisplayName = "Private Chambers";
                if (!snap.SectionPinned) snap.Section = Section.FreeCompany;
                return;

            case EstateType.SharedEstate:
                // Somebody else's house, so it is named for where it is and nothing more.
                // Two of them both read "Estate Hall", which told them apart from nothing;
                // the address does, and it is the one thing about the place that is not
                // personal to its owner. The owner's name is deliberately never recorded.
                snap.DisplayName = snap.Address;
                if (!snap.SectionPinned) snap.Section = Section.PrivateEstate;
                return;

            case EstateType.ApartmentRoom:
            case EstateType.ApartmentBuilding:
                // No name on it. It already sits inside its owner's section of the list.
                snap.DisplayName = "Apartment";
                if (!snap.SectionPinned) snap.Section = Section.PrivateEstate;
                return;
        }

        // No teleport entry matched. What the address alone can still settle:
        if (k.IsApartment)
        {
            snap.DisplayName = "Apartment";
            if (!snap.SectionPinned) snap.Section = Section.PrivateEstate;
            return;
        }

        // A room that is not an apartment is a chamber inside a Free Company house.
        if (k.Room > 0)
        {
            snap.DisplayName = "Private Chambers";
            if (!snap.SectionPinned) snap.Section = Section.FreeCompany;
            return;
        }

        // A plot could be personal, Free Company or shared, and without the teleport list
        // there is nothing here that says which. Personal is the common case; the section
        // is editable precisely because this line is a guess.
        snap.DisplayName = snap.Outdoors ? "Estate Yard" : "Estate Hall";
        if (!snap.SectionPinned) snap.Section = Section.PrivateEstate;
    }

    /// <summary>
    /// "Private Estate (Owned, Small)" - a name with whatever is known about it in one
    /// pair of brackets. Anything empty is left out, so a size that could not be told
    /// gives "Private Estate (Owned)" rather than a stray comma.
    /// </summary>
    private static string Tagged(string kind, params string[] tags)
    {
        var known = Array.FindAll(tags, t => !string.IsNullOrWhiteSpace(t));
        return known.Length == 0 ? kind : $"{kind} ({string.Join(", ", known)})";
    }

    /// <summary>
    /// Small, Medium or Large, from the interior the house uses.
    ///
    /// Each district has one interior zone per size, and the game names them in a fixed
    /// pattern - s1i1, s1i2, s1i3 for Mist's small, medium and large - so the size is the
    /// last digit of the zone's own name. Read from the sheet rather than from a list of
    /// zone numbers, so a new district needs nothing added here. Empty for anything that
    /// does not fit the pattern, a yard included: outdoors the zone is the whole ward.
    /// </summary>
    private string SizeOf(ushort territoryTypeId)
    {
        try
        {
            foreach (var row in data.GetExcelSheet<TerritoryType>())
            {
                if (row.RowId != territoryTypeId) continue;

                var name = row.Name.ExtractText();
                if (name.Length < 2 || name[^2] != 'i') return string.Empty;

                return name[^1] switch
                {
                    '1' => "Small",
                    '2' => "Medium",
                    '3' => "Large",
                    _ => string.Empty,
                };
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[FMF] Could not size territory {territoryTypeId}: {ex.Message}");
        }

        return string.Empty;
    }

    /// <summary>
    /// The logged-in character's Free Company, by name and by id.
    ///
    /// The name is what anyone reads; the id is what the chest is filed under, because a
    /// company can be renamed and two companies on different worlds can share a name.
    /// Empty when the proxy has not been populated - no company, or not loaded yet - and
    /// every caller treats empty as "do not claim to know".
    /// </summary>
    public static string FreeCompanyName() => Company(out var name, out _) ? name : string.Empty;

    public static ulong FreeCompanyId() => Company(out _, out var id) ? id : 0;

    private static bool Company(out string name, out ulong id)
    {
        name = string.Empty;
        id = 0;

        try
        {
            var info = InfoModule.Instance();
            if (info == null) return false;

            var proxy = (InfoProxyFreeCompany*)info->GetInfoProxyById(InfoProxyId.FreeCompany);
            if (proxy == null || proxy->Id == 0) return false;

            name = proxy->NameString;
            id = proxy->Id;
            return !string.IsNullOrWhiteSpace(name);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>District, ward, and whichever of plot or room applies.</summary>
    private string Address(PropertyKey k)
    {
        var parts = new List<string> { District(k.TerritoryTypeId) };

        // The game counts wards and plots from zero and its own windows count from one,
        // so both are shifted for display - printed raw, every address was one short of
        // the placard and Ward 1 and Plot 1 vanished altogether. The stored key keeps the
        // game's numbers; only what is shown changes. Negative means "not on one".
        if (k.Ward >= 0) parts.Add($"Ward {k.Ward + 1}");
        if (k.Division == 2) parts.Add("Subdivision");

        if (k.IsApartment)
            parts.Add(k.Room > 0 ? $"Apartment {k.Room}" : "Apartment");
        else if (k.Room > 0)
            parts.Add($"Room {k.Room}");
        else if (k.Plot >= 0)
            parts.Add($"Plot {k.Plot + 1}");

        return string.Join(", ", parts);
    }

    private string District(ushort territoryTypeId)
    {
        try
        {
            foreach (var row in data.GetExcelSheet<TerritoryType>())
            {
                if (row.RowId != territoryTypeId) continue;
                var name = row.PlaceName.Value.Name.ExtractText();
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[FMF] Could not name territory {territoryTypeId}: {ex.Message}");
        }

        return $"Territory {territoryTypeId}";
    }
}
