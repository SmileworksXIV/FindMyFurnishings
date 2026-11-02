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

    /// <summary>Bags, saddlebags and the FC chest, keyed by their own id.</summary>
    public Dictionary<string, HoldingSnapshot> Holdings { get; set; } = new();

    /// <summary>
    /// Search hides anything already placed. Remembered between sessions, because someone
    /// who searches for what they can still put down tends to keep doing it.
    /// </summary>
    public bool SearchStoredOnly { get; set; }

    /// <summary>
    /// Collapse identical rows into one with a count.
    ///
    /// Six of the same lamp in the same room is one fact. Listing it six times reads like
    /// a fault in the list rather than six lamps.
    /// </summary>
    public bool StackIdentical { get; set; } = true;

    /// <summary>
    /// Items you are collecting, by item id. A shopping list: tick what you are after and
    /// every place it sits stays on screen, so retrieving six things from four properties
    /// does not mean six searches and remembering four answers.
    /// </summary>
    public List<uint> Watched { get; set; } = new();

    /// <summary>
    /// How many DISTINCT furnishings the list holds.
    ///
    /// The list is item ids, and ToggleWatched removes before it adds, so an id can only
    /// ever appear once - six of the same lamp is one entry, not six. DedupeWatched keeps
    /// that true against a config file that was edited by hand or written by an older
    /// build, because a duplicate would silently eat one of the fifty.
    /// </summary>
    public const int WatchLimit = 50;

    /// <summary>Drops any repeated ids, keeping the order they were ticked in.</summary>
    public bool DedupeWatched()
    {
        var unique = Watched.Distinct().ToList();
        if (unique.Count == Watched.Count) return false;

        Watched = unique;
        return true;
    }

    public bool IsWatched(uint itemId) => Watched.Contains(itemId);

    /// <returns>False when the list is full and the item was not added.</returns>
    public bool ToggleWatched(uint itemId)
    {
        if (Watched.Remove(itemId)) return true;
        if (Watched.Count >= WatchLimit) return false;

        Watched.Add(itemId);
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

    // --- Behaviour ---
    public bool AutoIndexBags { get; set; } = true;

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

    public void Save(IDalamudPluginInterface pi)
    {
        Revision++;
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
