using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace FindMyFurnishings;

/// <summary>
/// Furnishings you own but have not placed: in your bags, your saddlebag, or a retainer's.
///
/// Only furnishing items are recorded. This is a furniture index, and a complete dump of
/// a container belongs in a different plugin.
/// </summary>
public sealed unsafe class InventoryScanner
{
    private static readonly InventoryType[] Bags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    private static readonly InventoryType[] Saddle =
    [
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];

    /// <summary>
    /// A retainer's bags. Only readable while that retainer is open at a bell - the client
    /// holds one retainer's inventory at a time, so this is the same visit-based deal as
    /// housing: whichever retainer you last opened is the one that got recorded.
    /// </summary>
    private static readonly InventoryType[] RetainerBags =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2,
        InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    private readonly FurnitureSheet sheet;
    private readonly IPluginLog log;

    public InventoryScanner(FurnitureSheet sheet, IPluginLog log)
    {
        this.sheet = sheet;
        this.log = log;
    }

    /// <summary>
    /// The character named in a container's id, where there is one.
    ///
    /// "bags.Sarna Umbra" and "saddle.Current Mood" carry it. "retainer.Flykra" does not
    /// - a retainer's id is the retainer's own name. Empty for those, which is the honest
    /// answer rather than a guess.
    /// </summary>
    public static string OwnerFromId(string id)
    {
        foreach (var prefix in new[] { "bags.", "saddle." })
            if (id.StartsWith(prefix, StringComparison.Ordinal))
                return id[prefix.Length..];

        return string.Empty;
    }

    /// <summary>
    /// Which section a container belongs to, derived from its id.
    ///
    /// Derived rather than stored, because a container's section is only written when it
    /// is re-scanned - and a retainer is only readable while it is open at a bell. Without
    /// this, retainers you had not visited since the Retainers section existed would sit
    /// in the wrong place indefinitely.
    /// </summary>
    public static Section SectionOf(string id) =>
        id.StartsWith("retainer.", StringComparison.Ordinal) ? Section.Retainers
        : Section.Inventory;

    public HoldingSnapshot? Bag(string owner) =>
        Read($"bags.{owner}", $"{owner}'s inventory", Bags, Spot.Inventory, "Bag",
             owner: owner);

    public HoldingSnapshot? Saddlebag(string owner) =>
        Read($"saddle.{owner}", $"{owner}'s saddlebag", Saddle, Spot.Saddlebag, "Saddlebag",
             owner: owner);

    /// <summary>
    /// The retainer currently open, if any. Each retainer is stored under its own name, so
    /// opening a second one adds to the index rather than replacing the first.
    /// </summary>
    public HoldingSnapshot? Retainer(string owner)
    {
        try
        {
            var m = RetainerManager.Instance();
            if (m == null) return null;

            var r = m->GetActiveRetainer();
            if (r == null)
            {
                trustedRetainer = 0;
                candidateRetainer = 0;
                return null;
            }

            var name = r->NameString;
            if (string.IsNullOrEmpty(name)) return null;

            if (!BagsBelongTo(r->RetainerId, r->ItemCount, name)) return null;

            // A retainer is only reachable with its window up, so an empty read here is a
            // real empty read.
            return Read($"retainer.{name}", $"{name} (retainer)", RetainerBags, Spot.Retainer,
                        "Page", Section.Retainers, trustEmpty: true, owner: owner);
        }
        catch (Exception ex)
        {
            log.Warning($"[FMF] Could not read the open retainer: {ex.Message}");
            return null;
        }
    }

    /// <summary>The retainer whose bags have been confirmed as their own this visit.</summary>
    private ulong trustedRetainer;

    /// <summary>A retainer that has been selected but whose bags are not yet believed.</summary>
    private ulong candidateRetainer;
    private DateTime candidateSince = DateTime.MinValue;

    /// <summary>
    /// How long to wait for a retainer's bags to agree with their item count before taking
    /// them as they are.
    /// </summary>
    private static readonly TimeSpan RetainerSettle = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Whether the retainer bags on the client are this retainer's yet.
    ///
    /// The client holds one retainer's bags at a time and does not clear them between
    /// retainers. Open a second one straight after the first and, until the server has
    /// answered, the game names the new retainer while the bags still hold the old one's
    /// things - which is how one retainer's furnishings were briefly filed under another.
    ///
    /// The retainer list carries each retainer's item count, so the bags are believed once
    /// they hold that many. The count is only used for this first look: it is not kept up
    /// to date while you move things in and out, so once a retainer's bags have been
    /// accepted they are trusted until a different retainer is opened. And if the two
    /// numbers never agree, the bags are taken as they stand after a few seconds - late is
    /// better than never recording a retainer at all.
    /// </summary>
    private bool BagsBelongTo(ulong retainerId, int expectedItems, string name)
    {
        if (retainerId == trustedRetainer) return true;

        var now = DateTime.UtcNow;

        if (retainerId != candidateRetainer)
        {
            candidateRetainer = retainerId;
            candidateSince = now;
        }

        var held = OccupiedSlots(RetainerBags);

        if (held != expectedItems && now - candidateSince < RetainerSettle)
        {
            log.Debug($"[FMF] {name}: bags hold {held} item(s), the retainer has " +
                      $"{expectedItems} - still the previous retainer's, waiting.");
            return false;
        }

        trustedRetainer = retainerId;
        return true;
    }

    private static int OccupiedSlots(InventoryType[] pages)
    {
        var inv = InventoryManager.Instance();
        if (inv == null) return 0;

        var used = 0;

        foreach (var page in pages)
        {
            var c = inv->GetInventoryContainer(page);
            if (c == null || !c->IsLoaded) continue;

            for (var s = 0; s < c->Size; s++)
            {
                var slot = c->GetInventorySlot(s);
                if (slot != null && slot->ItemId != 0) used++;
            }
        }

        return used;
    }

    /// <summary>Whether a retainer is open, without paying for its name.</summary>
    public static bool RetainerOpen()
    {
        try
        {
            var m = RetainerManager.Instance();
            return m != null && m->GetActiveRetainer() != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a set of container pages.
    ///
    /// Returns null when NOTHING loaded - there is a real difference between "the chest is
    /// empty of furniture" and "the chest has never been opened this session", and only
    /// the first is worth writing into the index. A partial read is kept but flagged.
    /// </summary>
    /// <param name="trustEmpty">
    /// Whether a reading with nothing in it can be believed. IsLoaded is not enough on its
    /// own for the shared containers: they advertise themselves before the server has sent
    /// their contents, so a read taken too early is indistinguishable from an empty one.
    /// When this is false, an empty result is discarded rather than written.
    /// </param>
    private HoldingSnapshot? Read(string id, string display, InventoryType[] pages,
                                  Spot spot, string pageLabel,
                                  Section section = Section.Inventory,
                                  bool trustEmpty = true,
                                  string owner = "")
    {
        var inv = InventoryManager.Instance();
        if (inv == null) return null;

        var snap = new HoldingSnapshot
        {
            Id = id,
            DisplayName = display,
            Section = section,
            Owner = owner,
            TakenUtc = DateTime.UtcNow,
        };

        var loadedAny = false;

        // Any item at all, furnishing or not. A chest full of crafting materials and no
        // furniture is genuinely loaded; a chest that reads as completely empty has almost
        // certainly not arrived yet.
        var sawAnyItem = false;

        for (var p = 0; p < pages.Length; p++)
        {
            var c = inv->GetInventoryContainer(pages[p]);
            if (c == null || !c->IsLoaded)
            {
                snap.Partial = true;
                continue;
            }

            loadedAny = true;

            for (var s = 0; s < c->Size; s++)
            {
                var slot = c->GetInventorySlot(s);
                if (slot == null || slot->ItemId == 0) continue;

                sawAnyItem = true;
                if (!sheet.IsFurnishing(slot->ItemId)) continue;

                snap.Pieces.Add(sheet.Make(slot->ItemId, spot, (int)slot->Quantity, 0,
                                           $"{pageLabel} {p + 1}"));
            }
        }

        if (!loadedAny) return null;

        if (!trustEmpty && !sawAnyItem)
        {
            log.Debug($"[FMF] {display}: reads as empty and has not been opened - " +
                      "not recorded, in case it simply has not arrived.");
            return null;
        }

        log.Debug($"[FMF] {display}: {snap.Pieces.Count:N0} furnishing(s)" +
                  (snap.Partial ? ", some pages not loaded." : "."));
        return snap;
    }

    /// <summary>
    /// A cheap number that changes when any watched container does.
    ///
    /// No sheet lookups, no snapshot, no allocation - just a walk of the slots. That is
    /// what makes it affordable several times a second, which is in turn what makes a
    /// furnishing landing in your bag show up at once instead of whenever the next lazy
    /// sweep came round.
    /// </summary>
    public long RawSignature()
    {
        var inv = InventoryManager.Instance();
        if (inv == null) return 0;

        long sig = 0;

        Fold(inv, Bags, ref sig);
        Fold(inv, Saddle, ref sig);

        // Only the containers that are actually readable. Folding a retainer's bags in
        // while no retainer is open would make the number flap between visits and trigger
        // rescans that cannot learn anything.
        if (RetainerOpen()) Fold(inv, RetainerBags, ref sig);

        return sig;
    }

    private static void Fold(InventoryManager* inv, InventoryType[] pages, ref long sig)
    {
        foreach (var page in pages)
        {
            var c = inv->GetInventoryContainer(page);
            if (c == null || !c->IsLoaded) continue;

            for (var s = 0; s < c->Size; s++)
            {
                var slot = c->GetInventorySlot(s);
                if (slot == null || slot->ItemId == 0) continue;

                sig += slot->ItemId * 31L + slot->Quantity;
            }
        }
    }
}
