using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace FindMyFurnishings;

/// <summary>
/// Top-level grouping in the Furnishings list. Stored per entry rather than worked out on the
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

    /// <summary>
    /// The character who recorded this, for the places that are per-character.
    ///
    /// Two characters in one Free Company each have a chamber, and may each have an
    /// apartment. They are separate properties with separate contents, but the game calls
    /// them all "Apartment" and "Free Company Chamber", so a list of them was identical
    /// rows. The address underneath did tell them apart; a room number is not something
    /// anyone reads as a person.
    ///
    /// Stored rather than read live, because relabelling runs at startup across every
    /// property at once - using whoever is logged in at that moment would stamp their name
    /// onto everybody else's rooms.
    /// </summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>
    /// The Free Company this belongs to, for company property.
    ///
    /// An alt in a different company has a different hall, yard and chest, and the game
    /// names none of them after the company. Stored at capture time for the same reason
    /// as <see cref="Owner"/>: read live, it would be whichever company the character
    /// logged in at relabelling time happens to belong to.
    /// </summary>
    public string FreeCompany { get; set; } = string.Empty;

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
    /// How many furnishings this property's containers have room for, placed and stored.
    ///
    /// Counted from the containers themselves rather than from a table of estate sizes.
    /// The game hands over each page with its own slot count, so summing the pages this
    /// property is actually allowed gives a number nobody had to look up and that cannot
    /// drift when Square Enix changes a limit. Zero means the pages had not loaded when
    /// this was taken, and the UI then shows the bare count rather than inventing a
    /// denominator.
    /// </summary>
    public int PlacedCapacity { get; set; }

    public int StoredCapacity { get; set; }

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

    /// <summary>
    /// The character this belongs to.
    ///
    /// Not derivable from the id for every container: bags and saddlebags carry the
    /// character's name, but a retainer is keyed by the RETAINER's name and says nothing
    /// about who hired them. Recorded at read time instead, where the answer is known.
    /// Empty on anything stored before this existed, which files it under Unknown until
    /// its next read.
    /// </summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>The company this belongs to, for the Free Company chest.</summary>
    public string FreeCompany { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public Section Section { get; set; } = Section.Inventory;
    public DateTime TakenUtc { get; set; }
    public List<Piece> Pieces { get; set; } = new();
    public bool Partial { get; set; }
}

/// <summary>
/// How a character is written down: "Sarna Umbra (Cactuar)".
///
/// A name alone is not a character. The same name can exist on every world, and plenty of
/// players keep an alt of the same name on another one - recorded by name only, the two
/// shared a section, and one's bags overwrote the other's. The home world is part of who
/// they are, so it is part of the text everything is filed under.
///
/// Character names cannot contain brackets, which is what makes the world safe to append
/// and to take off again.
/// </summary>
public static class CharacterName
{
    public static string With(string name, string world) =>
        string.IsNullOrWhiteSpace(world) ? name : $"{name} ({world})";

    public static bool HasWorld(string label) =>
        label.EndsWith(')') && label.Contains(" (", System.StringComparison.Ordinal);

    /// <summary>The name without its world.</summary>
    public static string Bare(string label) =>
        HasWorld(label) ? label[..label.LastIndexOf(" (", System.StringComparison.Ordinal)] : label;

    /// <summary>The world, or empty where none was recorded.</summary>
    public static string World(string label) =>
        HasWorld(label)
            ? label[(label.LastIndexOf(" (", System.StringComparison.Ordinal) + 2)..^1]
            : string.Empty;

    /// <summary>
    /// Whether two characters could be on the same world. Unknown counts as possible, so
    /// a record made before worlds were kept is not hidden from anybody.
    /// </summary>
    public static bool SameWorld(string a, string b)
    {
        var x = World(a);
        var y = World(b);
        return x.Length == 0 || y.Length == 0 || x == y;
    }

    /// <summary>"Sarna Umbra's inventory (Cactuar)" - the possessive goes on the name, not the world.</summary>
    public static string Owns(string label, string thing)
    {
        var world = World(label);
        return world.Length == 0 ? $"{label}'s {thing}" : $"{Bare(label)}'s {thing} ({world})";
    }
}

/// <summary>
/// The one housing limit the plugin has to be told, because the game will not say.
///
/// A house reads every container it has, so its size is whatever the game hands over and
/// grows by itself if the game's does. Private Chambers and apartments cannot work that
/// way: they share their containers with whatever estate was loaded before them, and the
/// pages past their own allowance still hold that estate's furniture - reading them all
/// once filed a hall's storeroom under a chamber. So the plugin has to know where a room's
/// share ends, and nothing it can read gives that number.
///
/// It is written here once and used everywhere, for placed items and for storage alike.
/// If a patch raises what a chamber or an apartment holds, this is the only line to
/// change - and until it is changed, anything past the old figure is not recorded.
/// </summary>
public static class RoomLimits
{
    public const int Slots = 150;
}

/// <summary>One row in the search results: a piece plus where it was found.</summary>
/// <param name="Source">
/// Which snapshot the piece came from - "p:" plus a property's id, or "h:" plus a
/// container's. The name on screen can be changed by hand; this cannot, so it is what a
/// remembered line is tied to.
/// </param>
public sealed record Hit(string Source, string Location, DateTime TakenUtc, bool Partial, Piece Piece,
                         PlaceKind Kind = PlaceKind.Carried);

/// <summary>What sort of place a row is in, which decides the colour it is written in.</summary>
public enum PlaceKind
{
    /// <summary>Bags and the saddlebag. Left in the window's ordinary text colour.</summary>
    Carried,
    Retainer,
    Owned,
    Company,
    Guest,
}

/// <summary>
/// A text colour, as three plain numbers from 0 to 1. Not a vector: those are written to
/// the config file as fields, which not everything that reads the file back understands.
/// </summary>
public sealed class TextColor
{
    public float R { get; set; } = 1f;
    public float G { get; set; } = 1f;
    public float B { get; set; } = 1f;

    public void Set(float r, float g, float b)
    {
        R = r;
        G = g;
        B = b;
    }
}

/// <summary>
/// One ticked line: an item in one spot of one place. The same item somewhere else is a
/// different line, and has a tick box of its own.
/// </summary>
public sealed class WatchedLine
{
    public string Source { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public Spot Where { get; set; }
    public byte Stain { get; set; }
    public string Detail { get; set; } = string.Empty;

    /// <summary>Kept so the line can still be named after its snapshot has gone.</summary>
    public string Name { get; set; } = string.Empty;
    public uint IconId { get; set; }
}

/// <summary>
/// A named set of furnishings, kept for later. It holds which furnishings, never where
/// they were - the places are looked up fresh every time the list is shown, so a list
/// cannot go out of date when something is moved.
/// </summary>
public sealed class SavedList
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<SavedItem> Items { get; set; } = new();
}

/// <summary>Name and icon are kept so an item still reads properly with no copy recorded.</summary>
public sealed class SavedItem
{
    public uint ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint IconId { get; set; }
}
