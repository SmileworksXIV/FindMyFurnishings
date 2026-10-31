using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
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
    /// <summary>Interior storerooms, in page order. The exterior ones are separate.</summary>
    private static readonly InventoryType[] InteriorStore =
    [
        InventoryType.HousingInteriorStoreroom1, InventoryType.HousingInteriorStoreroom2,
        InventoryType.HousingInteriorStoreroom3, InventoryType.HousingInteriorStoreroom4,
        InventoryType.HousingInteriorStoreroom5, InventoryType.HousingInteriorStoreroom6,
        InventoryType.HousingInteriorStoreroom7, InventoryType.HousingInteriorStoreroom8,
        InventoryType.HousingInteriorStoreroom9, InventoryType.HousingInteriorStoreroom10,
        InventoryType.HousingInteriorStoreroom11,
    ];

    private static readonly InventoryType[] ExteriorStore =
    [
        InventoryType.HousingExteriorStoreroom, InventoryType.HousingExteriorStoreroom2,
    ];

    /// <summary>
    /// Items placed in this property, as the property's own containers.
    ///
    /// NOT read from the furniture manager's table. That table is not scoped to your
    /// estate - standing in a yard it holds every plot rendered around you, which indexed
    /// the neighbours' gardens as if they were yours. These containers belong to the
    /// estate itself: forty slots outdoors, twelve pages of fifty indoors, matching the
    /// game's own placement limits.
    /// </summary>
    private static readonly InventoryType[] InteriorPlaced =
    [
        InventoryType.HousingInteriorPlacedItems1, InventoryType.HousingInteriorPlacedItems2,
        InventoryType.HousingInteriorPlacedItems3, InventoryType.HousingInteriorPlacedItems4,
        InventoryType.HousingInteriorPlacedItems5, InventoryType.HousingInteriorPlacedItems6,
        InventoryType.HousingInteriorPlacedItems7, InventoryType.HousingInteriorPlacedItems8,
        InventoryType.HousingInteriorPlacedItems9, InventoryType.HousingInteriorPlacedItems10,
        InventoryType.HousingInteriorPlacedItems11, InventoryType.HousingInteriorPlacedItems12,
    ];

    private static readonly InventoryType[] ExteriorPlaced =
    [
        InventoryType.HousingExteriorPlacedItems, InventoryType.HousingExteriorPlacedItems2,
    ];

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
    public static bool CanIndexHere(bool housingWindowOpen, out string why)
    {
        why = "not in a property";

        var mgr = HousingManager.Instance();
        if (mgr == null) return false;
        if (!mgr->IsInside() && !mgr->IsOutside()) return false;

        var estate = EstateOf(mgr->GetCurrentHouseId().Id, out var diagnosis);

        if (estate == EstateType.SharedEstate)
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
        if (mgr == null || !CanIndexHere(housingWindowOpen, out _)) return string.Empty;
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
    public static EstateType? EstateOf(ulong houseId, out string diagnosis)
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

            // A Free Company chamber has no teleport entry of its own - you reach it
            // through the estate. So an unmatched property in the same world and ward as
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

    public PropertySnapshot? Capture(bool housingWindowOpen)
    {
        var mgr = HousingManager.Instance();
        if (mgr == null || !CanIndexHere(housingWindowOpen, out _)) return null;
        if (IsSharedEstate()) return null;

        var indoors = mgr->IsInside();
        var key = KeyOf(mgr);

        var snap = new PropertySnapshot
        {
            Key = key,
            Outdoors = !indoors,
            TakenUtc = DateTime.UtcNow,
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
                                           snap, pages);
        snap.StoreroomLoaded = ReadContainers(indoors ? InteriorStore : ExteriorStore,
                                              indoors ? Spot.IndoorStoreroom
                                                      : Spot.OutdoorStoreroom, snap, pages);

        snap.Partial = !snap.PlacedLoaded || !snap.StoreroomLoaded;
        snap.StoreroomPagesLoaded = LoadedStoreroomPages(indoors);

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
    private bool ReadContainers(InventoryType[] pages, Spot spot, PropertySnapshot snap,
                                int maxPages = int.MaxValue)
    {
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
                snap.DisplayName = snap.Outdoors ? "Private Estate Yard" : "Estate Hall (Private)";
                if (!snap.SectionPinned) snap.Section = Section.PrivateEstate;
                return;

            case EstateType.FreeCompanyEstate:
                snap.DisplayName = snap.Outdoors
                    ? "Free Company Yard"
                    : "Estate Hall (Free Company)";
                if (!snap.SectionPinned) snap.Section = Section.FreeCompany;
                return;

            case EstateType.PersonalChambers:
                snap.DisplayName = "Free Company Chamber";
                if (!snap.SectionPinned) snap.Section = Section.FreeCompany;
                return;

            case EstateType.ApartmentRoom:
            case EstateType.ApartmentBuilding:
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
            snap.DisplayName = "Free Company Chamber";
            if (!snap.SectionPinned) snap.Section = Section.FreeCompany;
            return;
        }

        // A plot could be personal, Free Company or shared, and without the teleport list
        // there is nothing here that says which. Personal is the common case; the section
        // is editable precisely because this line is a guess.
        snap.DisplayName = snap.Outdoors ? "Estate Yard" : "Estate Hall";
        if (!snap.SectionPinned) snap.Section = Section.PrivateEstate;
    }

    /// <summary>District, ward, and whichever of plot or room applies.</summary>
    private string Address(PropertyKey k)
    {
        var parts = new List<string> { District(k.TerritoryTypeId) };

        if (k.Ward > 0) parts.Add($"Ward {k.Ward}");
        if (k.Division == 2) parts.Add("Subdivision");

        if (k.IsApartment)
            parts.Add(k.Room > 0 ? $"Apartment {k.Room}" : "Apartment");
        else if (k.Room > 0)
            parts.Add($"Room {k.Room}");
        else if (k.Plot > 0)
            parts.Add($"Plot {k.Plot}");

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
