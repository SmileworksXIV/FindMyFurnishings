using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FindMyFurnishings;

/// <summary>
/// Screenshot mode: every character and retainer name on screen swapped for a stand-in.
///
/// It works on a copy. The window is pointed at a duplicate of the index with the names
/// rewritten, and the real one is never touched - recording carries on into it as usual,
/// and nothing done to the copy is written to disk. Turning the mode off, or reloading
/// the plugin, throws the copy away.
///
/// Kept in one file on purpose, so it can be lifted out again in one piece.
/// </summary>
public sealed class Demo
{
    private const string CharacterKey = "c:";
    private const string RetainerKey = "r:";

    private static readonly string[] CharacterDefaults =
        ["Meteor Survivor", "Sample Alt", "Another Alt", "Third Alt", "Fourth Alt"];

    private static readonly string[] RetainerDefaults =
        ["Retainer One", "Retainer Two", "Retainer Three", "Retainer Four", "Retainer Five",
         "Retainer Six", "Retainer Seven", "Retainer Eight", "Retainer Nine", "Retainer Ten"];

    /// <summary>The stand-in for each real name. Kept between rebuilds so an edit sticks.</summary>
    public Dictionary<string, string> Aliases { get; } = new();

    /// <summary>Every name that has a stand-in, with the real name to label its box.</summary>
    public List<(string Key, string Real)> Names { get; private set; } = new();

    /// <summary>The rewritten copy the window reads from, or null when the mode is off.</summary>
    public Configuration? Config { get; private set; }

    /// <summary>The logged-in character, under their stand-in.</summary>
    public string Me { get; private set; } = string.Empty;

    public bool On => Config != null;

    public void Off()
    {
        Config = null;
        Configuration.Revision++;
    }

    public void Build(Configuration real, string me)
    {
        Collect(real, me);

        var copy = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(real))
                   ?? throw new InvalidOperationException("The index could not be copied.");
        copy.MarkDemo();

        foreach (var p in copy.Properties.Values)
        {
            p.Owner = Character(p.Owner);

            // A name typed in by hand could be anything, a person's included. The address
            // underneath is what the plugin recorded itself.
            p.CustomName = string.Empty;
        }

        var moved = new Dictionary<string, string>();
        var holdings = new Dictionary<string, HoldingSnapshot>();

        foreach (var h in copy.Holdings.Values)
        {
            var was = h.Id;
            var owner = Character(h.Owner);
            var retainer = RetainerOf(h);

            if (retainer.Length > 0)
            {
                var alias = Alias(RetainerKey + retainer);
                h.DisplayName = $"{alias} (retainer)";
                h.Id = InventoryScanner.RetainerId(alias, owner);
            }
            else
            {
                var bare = CharacterName.Bare(h.Owner);
                if (bare.Length > 0)
                {
                    var alias = Alias(CharacterKey + bare);
                    h.DisplayName = h.DisplayName.Replace(bare, alias, StringComparison.Ordinal);
                    h.Id = h.Id.Replace(bare, alias, StringComparison.Ordinal);
                }
            }

            h.Owner = owner;
            moved[was] = h.Id;
            holdings[h.Id] = h;
        }

        copy.Holdings = holdings;

        // Remembered lines point at a container by its id, which has just changed.
        foreach (var w in copy.WatchedLines)
            if (w.Source.StartsWith("h:", StringComparison.Ordinal)
                && moved.TryGetValue(w.Source[2..], out var to))
                w.Source = "h:" + to;

        copy.CompanyOf = copy.CompanyOf
            .GroupBy(k => Character(k.Key))
            .ToDictionary(g => g.Key, g => g.First().Value);

        copy.SearchExcluded = copy.SearchExcluded.Select(Character).Distinct().ToList();

        Me = Character(me);
        Config = copy;
        Configuration.Revision++;
    }

    /// <summary>Finds every name in the index and gives a stand-in to any without one.</summary>
    private void Collect(Configuration real, string me)
    {
        var characters = real.Properties.Values.Select(p => p.Owner)
            .Concat(real.Holdings.Values.Select(h => h.Owner))
            .Concat(real.CompanyOf.Keys)
            .Append(me)
            .Select(CharacterName.Bare)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .OrderByDescending(n => n == CharacterName.Bare(me))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var retainers = real.Holdings.Values.Select(RetainerOf)
            .Where(n => n.Length > 0)
            .Distinct()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var names = new List<(string, string)>();

        for (var i = 0; i < characters.Count; i++)
        {
            var key = CharacterKey + characters[i];
            Aliases.TryAdd(key, i < CharacterDefaults.Length ? CharacterDefaults[i] : $"Alt {i + 1}");
            names.Add((key, characters[i]));
        }

        for (var i = 0; i < retainers.Count; i++)
        {
            var key = RetainerKey + retainers[i];
            Aliases.TryAdd(key, i < RetainerDefaults.Length ? RetainerDefaults[i] : $"Retainer {i + 1}");
            names.Add((key, $"{retainers[i]} (retainer)"));
        }

        Names = names;
    }

    /// <summary>A box left empty must not fall back to the real name.</summary>
    private string Alias(string key) =>
        Aliases.TryGetValue(key, out var a) && !string.IsNullOrWhiteSpace(a) ? a.Trim() : "Name";

    /// <summary>"Real Name (World)" under its stand-in, keeping the world.</summary>
    private string Character(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return label;

        var bare = CharacterName.Bare(label);
        return CharacterName.With(Alias(CharacterKey + bare), CharacterName.World(label));
    }

    /// <summary>A retainer's name out of its id, "retainer.Name@Owner". Empty for anything else.</summary>
    private static string RetainerOf(HoldingSnapshot h)
    {
        const string prefix = "retainer.";
        if (!h.Id.StartsWith(prefix, StringComparison.Ordinal)) return string.Empty;

        var rest = h.Id[prefix.Length..];
        var at = rest.IndexOf('@');
        return at >= 0 ? rest[..at] : rest;
    }
}
