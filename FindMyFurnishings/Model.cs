using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace FindMyFurnishings;

/// <summary>
/// Top-level grouping in the Places list. Stored per entry rather than worked out on the
/// fly, because the game does not tell us whether a plot is a personal estate or a Free
/// Company house - so the automatic guess has to be correctable and has to stick.
/// </summary>
public enum Section
{
    PrivateEstate = 0,
    FreeCompany = 1,

    // 2 was SharedEstate, removed once shared estates stopped being indexed at all.
    // Inventory keeps its number so configs written before that still load correctly.
    Inventory = 3,
    Retainers = 4,
}

public static class SectionNames
{
    public static readonly Section[] All =
        [Section.PrivateEstate, Section.FreeCompany, Section.Inventory, Section.Retainers];

    public static string Label(Section s) => s switch
    {
        Section.PrivateEstate => "Private Estates",
        Section.FreeCompany   => "Free Company",
        Section.Inventory     => "Inventory",
        Section.Retainers     => "Retainers",
        _                     => "Other",
    };
}

/// <summary>Where a furnishing was found. Ordered roughly by "how placed" it is.</summary>
public enum Spot
{
    IndoorPlaced,
    OutdoorPlaced,
    IndoorStoreroom,
    OutdoorStoreroom,
    Inventory,
    Saddlebag,
    Retainer,
    FreeCompanyChest,
}

public static class SpotNames
{
    /// <summary>Set down in the room or the yard, as opposed to sitting in storage.</summary>
    public static bool IsPlaced(Spot s) => s is Spot.IndoorPlaced or Spot.OutdoorPlaced;

    public static string Label(Spot s) => s switch
    {
        Spot.IndoorPlaced     => "Placed indoors",
        Spot.OutdoorPlaced    => "Placed in the yard",
        // One name for both. Interior and exterior are separate property entries already,
        // so repeating it on every row said nothing the heading had not.
        Spot.IndoorStoreroom  => "Storeroom",
        Spot.OutdoorStoreroom => "Storeroom",
        Spot.Inventory        => "Inventory",
        Spot.Saddlebag        => "Saddlebag",
        Spot.Retainer         => "Retainer",
        Spot.FreeCompanyChest => "FC chest",
        _                     => "Unknown",
    };
}

/// <summary>
/// One furnishing in one place. Quantity matters for stored items - a storeroom slot can
/// hold several of the same chair - while a placed item is always a single object.
/// </summary>
public sealed class Piece
{
    public uint ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint IconId { get; set; }
    public int Quantity { get; set; } = 1;
    public Spot Where { get; set; }

    /// <summary>Dye applied to a placed piece. 0 means undyed; only placed items carry one.</summary>
    public byte Stain { get; set; }
    public string StainName { get; set; } = string.Empty;

    /// <summary>Free text for the container page, e.g. "Storeroom 3". Empty for placed items.</summary>
    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// Identity of a property. Stored rather than recomputed, because the whole point of the
/// index is to describe places you are not currently standing in.
/// </summary>
public sealed class PropertyKey
{
    public ushort WorldId { get; set; }
    public ushort TerritoryTypeId { get; set; }
    public sbyte Ward { get; set; }
    public sbyte Plot { get; set; }
    public byte Division { get; set; }
    public short Room { get; set; }
    public bool IsApartment { get; set; }

    /// <summary>
    /// Stable identity for dictionary storage. Deliberately not the display name: place
    /// names are localised and can change, whereas these numbers address the plot itself.
    /// </summary>
    public string Id =>
        $"w{WorldId}.t{TerritoryTypeId}.d{Division}.ward{Ward}.plot{Plot}.room{Room}";
}

/// <summary>
/// Everything one property held the last time it was looked at.
///
/// A snapshot is explicitly dated and the UI always shows that date. The game only holds
/// furnishing data for the property you are standing in, so every answer this plugin gives
/// about somewhere else is a memory - presenting it as current would be a lie.
/// </summary>
public sealed class PropertySnapshot
{
    public PropertyKey Key { get; set; } = new();

    /// <summary>The default friendly name - "Apartment", "Personal Estate - Mist, ...".</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The full address, always shown under the name. Several properties can share a
    /// friendly name - two apartments are both "Apartment" - and this is what tells you
    /// which one you are actually looking at.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// The yard rather than the interior. They are separate places with separate
    /// furnishings, so they are separate entries and have to be told apart by name.
    /// </summary>
    public bool Outdoors { get; set; }

    public Section Section { get; set; } = Section.PrivateEstate;

    /// <summary>
    /// Set once you choose a section yourself. Automatic filing then leaves it alone -
    /// without this, relabelling on startup quietly undid the choice on every launch.
    /// </summary>
    public bool SectionPinned { get; set; }

    /// <summary>
    /// What the game calls this kind of estate, once a teleport entry has confirmed it.
    /// Kept so a later visit cannot undo a correct name just because the teleport list
    /// happened to be empty at that moment.
    /// </summary>
    public EstateType? Estate { get; set; }

    /// <summary>
    /// A name you gave it. Survives re-capture - the address is what identifies the
    /// property, so a rename is yours to keep however many times the contents change.
    /// </summary>
    public string CustomName { get; set; } = string.Empty;

    /// <summary>The rename if there is one, otherwise the address.</summary>
    public string Label => string.IsNullOrWhiteSpace(CustomName) ? DisplayName : CustomName;

    public DateTime TakenUtc { get; set; }
    public List<Piece> Pieces { get; set; } = new();

    /// <summary>
    /// How many storeroom pages had arrived when this was taken. Kept so that opening the
    /// storeroom later can be recognised as new information rather than a repeat visit.
    /// </summary>
    public int StoreroomPagesLoaded { get; set; }

    /// <summary>
    /// Whether the estate's placed-furniture containers had loaded.
    ///
    /// Indoors they often have not until the housing menu is opened, and the difference
    /// matters: a snapshot missing its storeroom is incomplete, while one missing its
    /// placed furniture is empty of the very thing you came to look at.
    /// </summary>
    public bool PlacedLoaded { get; set; }

    /// <summary>Whether the storeroom containers had loaded. Separate from placed: they arrive later.</summary>
    public bool StoreroomLoaded { get; set; }

    /// <summary>
    /// Set when some storeroom pages had not loaded at snapshot time. The counts are then
    /// known to be incomplete, and the UI says so rather than implying the room is empty.
    /// </summary>
    public bool Partial { get; set; }
}

/// <summary>A non-housing container: your bags, saddlebag, or the FC chest.</summary>
public sealed class HoldingSnapshot
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public Section Section { get; set; } = Section.Inventory;
    public DateTime TakenUtc { get; set; }
    public List<Piece> Pieces { get; set; } = new();
    public bool Partial { get; set; }
}

/// <summary>One row in the search results: a piece plus where it was found.</summary>
public sealed record Hit(string Location, DateTime TakenUtc, bool Partial, Piece Piece);
