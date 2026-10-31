using System;
using System.Collections.Generic;
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
    /// Items you are collecting, by item id. A shopping list: tick what you are after and
    /// every place it sits stays on screen, so retrieving six things from four properties
    /// does not mean six searches and remembering four answers.
    /// </summary>
    public List<uint> Watched { get; set; } = new();

    public const int WatchLimit = 50;

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

    public void Save(IDalamudPluginInterface pi) => pi.SavePluginConfig(this);

    public void Forget(string propertyId) => Properties.Remove(propertyId);

    public void ForgetEverything()
    {
        Properties.Clear();
        Holdings.Clear();
    }

    public bool IsStale(DateTime takenUtc) =>
        StaleAfterDays > 0 && (DateTime.UtcNow - takenUtc).TotalDays >= StaleAfterDays;
}
