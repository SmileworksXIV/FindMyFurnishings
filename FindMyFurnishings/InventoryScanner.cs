using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace FindMyFurnishings;

/// <summary>
/// Furnishings you own but have not placed: in your bags, your saddlebag, or the Free
/// Company chest.
///
/// Only furnishing items are recorded. This is a furniture index, and a complete dump of
/// the FC chest belongs in a different plugin.
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

    private static readonly InventoryType[] FcChest =
    [
        InventoryType.FreeCompanyPage1, InventoryType.FreeCompanyPage2,
        InventoryType.FreeCompanyPage3, InventoryType.FreeCompanyPage4,
        InventoryType.FreeCompanyPage5,
    ];

    private readonly FurnitureSheet sheet;
    private readonly IPluginLog log;

    public InventoryScanner(FurnitureSheet sheet, IPluginLog log)
    {
        this.sheet = sheet;
        this.log = log;
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
        : id == "fc" ? Section.FreeCompany
        : Section.Inventory;

    public HoldingSnapshot? Bag(string owner) =>
        Read($"bags.{owner}", $"{owner}'s inventory", Bags, Spot.Inventory, "Bag");

    public HoldingSnapshot? Saddlebag(string owner) =>
        Read($"saddle.{owner}", $"{owner}'s saddlebag", Saddle, Spot.Saddlebag, "Saddlebag");

    /// <summary>
    /// The retainer currently open, if any. Each retainer is stored under its own name, so
    /// opening a second one adds to the index rather than replacing the first.
    /// </summary>
    public HoldingSnapshot? Retainer()
    {
        var name = ActiveRetainerName();
        if (string.IsNullOrEmpty(name)) return null;

        return Read($"retainer.{name}", $"{name} (retainer)", RetainerBags, Spot.Retainer,
                    "Page", Section.Retainers);
    }

    /// <summary>
    /// Name of the retainer at the counter. Empty when none is open, which is also the
    /// signal not to try reading retainer bags at all.
    /// </summary>
    private static string ActiveRetainerName()
    {
        try
        {
            var m = RetainerManager.Instance();
            if (m == null) return string.Empty;

            var r = m->GetActiveRetainer();
            return r == null ? string.Empty : r->NameString;
        }
        catch
        {
            return string.Empty;
        }
    }

    public HoldingSnapshot? FreeCompany() =>
        Read("fc", "Free Company chest", FcChest, Spot.FreeCompanyChest, "Tab",
             Section.FreeCompany);

    /// <summary>
    /// Reads a set of container pages.
    ///
    /// Returns null when NOTHING loaded - there is a real difference between "the chest is
    /// empty of furniture" and "the chest has never been opened this session", and only
    /// the first is worth writing into the index. A partial read is kept but flagged.
    /// </summary>
    private HoldingSnapshot? Read(string id, string display, InventoryType[] pages,
                                  Spot spot, string pageLabel,
                                  Section section = Section.Inventory)
    {
        var inv = InventoryManager.Instance();
        if (inv == null) return null;

        var snap = new HoldingSnapshot
        {
            Id = id,
            DisplayName = display,
            Section = section,
            TakenUtc = DateTime.UtcNow,
        };

        var loadedAny = false;

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
                if (!sheet.IsFurnishing(slot->ItemId)) continue;

                snap.Pieces.Add(sheet.Make(slot->ItemId, spot, (int)slot->Quantity, 0,
                                           $"{pageLabel} {p + 1}"));
            }
        }

        if (!loadedAny) return null;

        log.Debug($"[FMF] {display}: {snap.Pieces.Count:N0} furnishing(s)" +
                  (snap.Partial ? ", some pages not loaded." : "."));
        return snap;
    }
}
