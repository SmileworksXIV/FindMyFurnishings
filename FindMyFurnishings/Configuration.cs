using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace FindMyFurnishings;

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;

    /// <summary>Every property ever visited, keyed by PropertyKey.Id.</summary>
    public Dictionary<string, PropertySnapshot> Properties { get; set; } = new();

    /// <summary>Bags, saddlebags and retainers, keyed by their own id.</summary>
    public Dictionary<string, HoldingSnapshot> Holdings { get; set; } = new();

    /// <summary>
    /// Search hides anything already placed. Remembered between sessions, because someone
    /// who searches for what they can still put down tends to keep doing it.
    /// </summary>
    public bool SearchStoredOnly { get; set; }

    /// <summary>
    /// List search results place by place instead of by item name - the order someone
    /// walking round collecting things wants them in.
    /// </summary>
    public bool SearchSortByLocation { get; set; }

    /// <summary>
    /// Characters left OUT of a search, as they are written in the list.
    ///
    /// Kept as the ones switched off rather than the ones switched on, so a character
    /// recorded for the first time is searched without anyone having to go and tick it.
    /// </summary>
    public List<string> SearchExcluded { get; set; } = new();

    /// <summary>Private estates, apartments and Private Chambers.</summary>
    public bool SearchOwnedEstates { get; set; } = true;

    /// <summary>Estates you have editing rights to but do not own.</summary>
    public bool SearchGuestEstates { get; set; } = true;

    /// <summary>The Free Company's hall and yard. Not its chambers - those are yours.</summary>
    public bool SearchCompanyEstates { get; set; } = true;

    /// <summary>
    /// Collapse identical rows into one with a count.
    ///
    /// Six of the same lamp in the same room is one fact. Listing it six times reads like
    /// a fault in the list rather than six lamps.
    /// </summary>
    public bool StackIdentical { get; set; } = true;

    /// <summary>
    /// The old remembered list, by item id. Ticking one copy of an item ticked every copy
    /// everywhere, because the id was all that was kept. Read once at load, turned into
    /// lines by <see cref="MigrateWatched"/>, and left empty from then on.
    /// </summary>
    public List<uint> Watched { get; set; } = new();

    /// <summary>
    /// Lines you are collecting. A shopping list: tick the copies you are going to fetch
    /// and they stay on screen, so a trip round four properties is one list rather than
    /// four searches and a memory test.
    /// </summary>
    public List<WatchedLine> WatchedLines { get; set; } = new();

    /// <summary>How many lines the list holds.</summary>
    public const int WatchLimit = 50;

    // --- Row colours. Storeroom wins: a stored piece is written in the storeroom colour
    // --- wherever it is, and the other four apply to pieces that are not stored.
    public TextColor ColorStoreroom { get; set; } = new() { R = 0.992f, G = 0.992f, B = 0.4f };
    public TextColor ColorInventory { get; set; } = new();
    public TextColor ColorRetainers { get; set; } = new();
    public TextColor ColorOwned { get; set; } = new();
    public TextColor ColorCompany { get; set; } = new();
    public TextColor ColorGuest { get; set; } = new();

    /// <summary>Back to how it looked before colours could be chosen: stored yellow, the rest white.</summary>
    public void ResetColors()
    {
        ColorStoreroom.Set(0.992f, 0.992f, 0.4f);
        ColorInventory.Set(1f, 1f, 1f);
        ColorRetainers.Set(1f, 1f, 1f);
        ColorOwned.Set(1f, 1f, 1f);
        ColorCompany.Set(1f, 1f, 1f);
        ColorGuest.Set(1f, 1f, 1f);
    }

    /// <summary>Remembered lists kept by name, on the Saved Lists tab.</summary>
    public List<SavedList> SavedLists { get; set; } = new();

    /// <summary>
    /// Keeps the furnishings on the remembered list as a new saved list. Six ticked lines
    /// of one lamp are one furnishing here: a saved list shows every copy anyway.
    /// </summary>
    /// <returns>The new list, or null when nothing is ticked.</returns>
    public SavedList? SaveWatchedAsList()
    {
        if (WatchedLines.Count == 0) return null;

        var n = SavedLists.Count + 1;
        while (SavedLists.Any(l => l.Name == $"List {n}")) n++;

        var list = new SavedList { Id = Guid.NewGuid().ToString("N"), Name = $"List {n}" };

        foreach (var w in WatchedLines)
            if (list.Items.All(i => i.ItemId != w.ItemId))
                list.Items.Add(new SavedItem { ItemId = w.ItemId, Name = w.Name, IconId = w.IconId });

        SavedLists.Add(list);
        return list;
    }

    /// <summary>
    /// Where this piece sits in the list, or -1. Compared field by field, numbers first,
    /// because it is asked once per row per frame and must not build a string to answer.
    /// </summary>
    public int FindWatched(string source, Piece p)
    {
        for (var i = 0; i < WatchedLines.Count; i++)
        {
            var w = WatchedLines[i];
            if (w.ItemId == p.ItemId && w.Where == p.Where && w.Stain == p.Stain
                && w.Detail == p.Detail && w.Source == source)
                return i;
        }

        return -1;
    }

    public bool IsWatched(string source, Piece p) => FindWatched(source, p) >= 0;

    /// <returns>False when the list is full and the line was not added.</returns>
    public bool ToggleWatched(string source, Piece p)
    {
        var at = FindWatched(source, p);
        if (at >= 0)
        {
            WatchedLines.RemoveAt(at);
            return true;
        }

        if (WatchedLines.Count >= WatchLimit) return false;

        AddWatched(source, p);
        return true;
    }

    private void AddWatched(string source, Piece p) =>
        WatchedLines.Add(new WatchedLine
        {
            Source = source,
            ItemId = p.ItemId,
            Where = p.Where,
            Stain = p.Stain,
            Detail = p.Detail,
            Name = p.Name,
            IconId = p.IconId,
        });

    /// <summary>
    /// Turns an old item-id list into lines: every place each item currently sits, which
    /// is exactly what the old list showed. Not held to the limit - nothing already
    /// remembered should vanish because of an update.
    /// </summary>
    public bool MigrateWatched()
    {
        if (Watched.Count == 0) return false;

        var ids = new HashSet<uint>(Watched);

        foreach (var p in Properties.Values)
        {
            var source = "p:" + p.Key.Id;
            foreach (var piece in p.Pieces)
                if (ids.Contains(piece.ItemId) && !IsWatched(source, piece))
                    AddWatched(source, piece);
        }

        foreach (var h in Holdings.Values)
        {
            var source = "h:" + h.Id;
            foreach (var piece in h.Pieces)
                if (ids.Contains(piece.ItemId) && !IsWatched(source, piece))
                    AddWatched(source, piece);
        }

        Watched.Clear();
        return true;
    }

    /// <summary>
    /// Confirm each automatic recording in chat.
    ///
    /// A property records itself whenever its Housing window opens, which is also every
    /// time you open that window to move one thing - so the confirmation can become noise.
    /// Turning it off silences only those: an explicit /fmf here still confirms, and
    /// anything that went wrong is still said.
    /// </summary>
    public bool AnnounceCaptures { get; set; } = true;

    /// <summary>
    /// Also record estates you only have editing rights on.
    ///
    /// This is YOUR setting about YOUR index, and it cannot be anything else. A Dalamud
    /// plugin sees only its own client, so nothing here asks the owner's permission or
    /// could be made to - the owner may not even run this plugin. It records only what
    /// the game already shows you when it lets you decorate, and never whose estate it is.
    /// </summary>
    public bool RecordSharedEstates { get; set; } = true;

    /// <summary>
    /// Record the Free Company's Estate Hall and yard. Private Chambers are your own room
    /// and are recorded either way.
    /// </summary>
    public bool RecordCompanyEstates { get; set; } = true;

    /// <summary>
    /// Which Free Company each character belongs to.
    ///
    /// Company property is the company's, not the character's who walked in - but a
    /// character does belong to a company, and that is what lets a hall appear under
    /// everyone actually in it rather than under whoever recorded it. Filled in as each
    /// character plays, so an alt not logged into since this existed simply has no entry
    /// and its company's property stays in its own group.
    /// </summary>
    public Dictionary<string, string> CompanyOf { get; set; } = new();

    /// <summary>Flag a snapshot as worth refreshing once it is this old. 0 disables the hint.</summary>
    public int StaleAfterDays { get; set; } = 30;

    /// <summary>
    /// Bumped on every write. Static, so it is never serialised and never survives a
    /// restart - it only has to be different from the last value the UI saw.
    ///
    /// The window used to rebuild its search results from every piece in every property
    /// on every frame, twice, because it had no way of knowing whether anything had
    /// changed since the last one. This is that way.
    /// </summary>
    public static int Revision;

    /// <summary>
    /// Set on the copy that screenshot mode shows. A private field, so it is never written
    /// to the config file and a loaded config can never have it set.
    /// </summary>
    private bool demo;

    public void MarkDemo() => demo = true;

    public void Save(IDalamudPluginInterface pi)
    {
        Revision++;

        // The demo copy has stand-in names in it. Written to disk, it would replace the
        // real index with them.
        if (demo) return;

        pi.SavePluginConfig(this);
    }

    public void Forget(string propertyId) => Properties.Remove(propertyId);

    public void ForgetEverything()
    {
        Properties.Clear();
        Holdings.Clear();
    }

    public bool IsStale(DateTime takenUtc) =>
        StaleAfterDays > 0 && (DateTime.UtcNow - takenUtc).TotalDays >= StaleAfterDays;
}
