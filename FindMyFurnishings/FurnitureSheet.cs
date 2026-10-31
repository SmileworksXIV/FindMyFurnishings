using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace FindMyFurnishings;

/// <summary>
/// Turns the game's furnishing identifiers into names you can read and search.
///
/// Placed furniture does NOT carry an item id. HousingFurniture.Id is an index into the
/// HousingFurniture sheet, and which half of that sheet depends on where the piece sits:
///
///     indoors   ->  HousingFurniture row (0x20000 | Id)
///     outdoors  ->  HousingFurniture row (0x30000 | Id)
///
/// That row then points at the real Item row, which is where the name and icon live.
/// Both tables are walked once at load and cached, rather than looked up per row: the
/// sheets are small, and an index rebuilt on every search would be the slow part of
/// searching.
/// </summary>
public sealed class FurnitureSheet
{
    private const uint IndoorBase = 0x20000;
    private const uint OutdoorBase = 0x30000;

    private readonly Dictionary<uint, Piece> byItemId = new();
    private readonly Dictionary<uint, uint> furnitureRowToItem = new();
    private readonly Dictionary<uint, uint> yardRowToItem = new();
    private readonly Dictionary<byte, string> stains = new();

    public bool Ready { get; private set; }
    public int FurnishingCount => byItemId.Count;

    public void Load(IDataManager data, IPluginLog log)
    {
        byItemId.Clear();
        furnitureRowToItem.Clear();
        yardRowToItem.Clear();
        stains.Clear();

        // Item first: everything else resolves into it.
        var items = new Dictionary<uint, (string Name, uint Icon)>();
        foreach (var row in data.GetExcelSheet<Item>())
        {
            var name = row.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) continue;
            items[row.RowId] = (name, row.Icon);
        }

        // The furnishing tables. A row with no item attached is a gap in the sheet, of
        // which there are many - skip rather than record an entry with a blank name.
        foreach (var row in data.GetExcelSheet<HousingFurniture>())
        {
            var itemId = row.Item.RowId;
            if (itemId == 0 || !items.TryGetValue(itemId, out var it)) continue;

            furnitureRowToItem[row.RowId] = itemId;
            byItemId.TryAdd(itemId, new Piece { ItemId = itemId, Name = it.Name, IconId = it.Icon });
        }

        // Yard furnishings live in their own sheet and are what you find on a plot
        // exterior. Without this, outdoor pieces resolve to nothing.
        try
        {
            foreach (var row in data.GetExcelSheet<HousingYardObject>())
            {
                var itemId = row.Item.RowId;
                if (itemId == 0 || !items.TryGetValue(itemId, out var it)) continue;

                yardRowToItem[row.RowId] = itemId;
                byItemId.TryAdd(itemId, new Piece { ItemId = itemId, Name = it.Name, IconId = it.Icon });
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[FMF] Yard furnishing sheet unavailable: {ex.Message}");
        }

        foreach (var row in data.GetExcelSheet<Stain>())
        {
            var name = row.Name.ExtractText();
            if (!string.IsNullOrEmpty(name) && row.RowId <= byte.MaxValue)
                stains[(byte)row.RowId] = name;
        }

        Ready = byItemId.Count > 0;
        log.Information($"[FMF] Indexed {byItemId.Count:N0} furnishings, " +
                        $"{furnitureRowToItem.Count:N0} placeable rows, " +
                        $"{yardRowToItem.Count:N0} yard rows, {stains.Count:N0} dyes.");
    }

    /// <summary>
    /// The item id behind a placed piece. <paramref name="indoors"/> picks which half of
    /// the HousingFurniture sheet the id addresses - the same raw number means different
    /// furniture inside and out.
    /// </summary>
    public uint ItemIdForPlaced(uint furnitureId, bool indoors)
    {
        var row = (indoors ? IndoorBase : OutdoorBase) | furnitureId;

        var id = furnitureRowToItem.GetValueOrDefault(row);
        if (id != 0) return id;

        // Yard-only objects - hedges, fences, flowerpots - are not in the furnishing
        // table at all. Tried only after the proper lookup misses, and never with the
        // opposite indoor/outdoor base: naming the wrong chair is worse than naming none.
        if (indoors) return 0;

        id = yardRowToItem.GetValueOrDefault(row);
        return id != 0 ? id : yardRowToItem.GetValueOrDefault(furnitureId);
    }

    /// <summary>Diagnostics: what each candidate lookup gives for a raw id.</summary>
    public string ExplainPlaced(uint furnitureId, bool indoors)
    {
        var row = (indoors ? IndoorBase : OutdoorBase) | furnitureId;
        return $"row=0x{row:X} furniture={furnitureRowToItem.GetValueOrDefault(row)} " +
               $"yard[row]={yardRowToItem.GetValueOrDefault(row)} " +
               $"yard[raw]={yardRowToItem.GetValueOrDefault(furnitureId)}";
    }

    /// <summary>True if this item is a furnishing at all - the filter for scanning bags.</summary>
    public bool IsFurnishing(uint itemId) => byItemId.ContainsKey(itemId);

    public string NameOf(uint itemId) =>
        byItemId.TryGetValue(itemId, out var p) ? p.Name : string.Empty;

    public uint IconOf(uint itemId) =>
        byItemId.TryGetValue(itemId, out var p) ? p.IconId : 0;

    public string StainName(byte stain) =>
        stain == 0 ? string.Empty : stains.GetValueOrDefault(stain, $"Dye {stain}");

    /// <summary>Builds a result row, resolving name and icon from the item id.</summary>
    public Piece Make(uint itemId, Spot where, int quantity = 1, byte stain = 0, string detail = "")
        => new()
        {
            ItemId = itemId,
            Name = NameOf(itemId),
            IconId = IconOf(itemId),
            Quantity = quantity,
            Where = where,
            Stain = stain,
            StainName = StainName(stain),
            Detail = detail,
        };
}
