using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace FindMyFurnishings;

public sealed class MainWindow : Window
{
    private const float IconSize = 24f;

    private readonly Plugin plugin;
    private string search = string.Empty;

    /// <summary>The furnishing whose name was clicked, while its "Add to list" menu is up.</summary>
    private Piece? addToList;

    /// <summary>
    /// The menu a furnishing's name opens: every saved list, to put it on or take it off,
    /// and a way to start a new list with it.
    /// </summary>
    private void DrawAddToList(string popup)
    {
        if (!ImGui.BeginPopup(popup)) return;

        var piece = addToList;
        if (piece == null)
        {
            ImGui.EndPopup();
            return;
        }

        ImGui.TextDisabled($"Add {piece.Name} to:");
        ImGui.Separator();

        foreach (var list in Cfg.SavedLists)
        {
            var on = list.Items.Any(i => i.ItemId == piece.ItemId);
            var name = string.IsNullOrWhiteSpace(list.Name) ? "Unnamed list" : list.Name;

            // Already on it: shown as chosen, and choosing it again takes it back off.
            if (!ImGui.Selectable($"{name}{(on ? "  (added)" : string.Empty)}###addto{list.Id}", on))
                continue;

            Cfg.ToggleOnList(list, piece);
            Cfg.Save(Plugin.PluginInterface);
        }

        if (Cfg.SavedLists.Count > 0) ImGui.Separator();

        if (ImGui.Selectable("New list"))
        {
            Cfg.NewListWith(piece);
            Cfg.Save(Plugin.PluginInterface);
        }

        ImGui.EndPopup();
    }

    /// <summary>The index, or the stand-in copy of it while screenshot mode is on.</summary>
    private Configuration Cfg => plugin.Demo.Config ?? plugin.Config;

    private string Me => plugin.Demo.On ? plugin.Demo.Me : Plugin.LocalPlayerName;

    public MainWindow(Plugin plugin)
        : base("Find My Furnishings###FmfMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 360),
            MaximumSize = new Vector2(1600, 1200),
        };
    }

    /// <summary>Set by /fmf &lt;text&gt;, so the Search tab comes forward once.</summary>
    private bool selectSearch;

    /// <summary>
    /// Opens the window on the Search tab with this already typed in. What "/fmf oak bed"
    /// was always described as doing.
    /// </summary>
    /// <summary>
    /// Writes what the search box is doing to the log: what is typed, what is switched
    /// off, and for every place recorded whether it is searched and how many of its
    /// pieces match. For working out why something recorded does not come up.
    /// </summary>
    public void DumpSearch(Dalamud.Plugin.Services.IPluginLog log, string me)
    {
        static string Plain(string s) =>
            string.Concat(s.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}"));

        var needle = search.Trim();

        log.Information("[FMF] ---- search ----");
        log.Information($"[FMF] typed=\"{Plain(search)}\" me=\"{Plain(me)}\" " +
                        $"demo={plugin.Demo.On} storedOnly={Cfg.SearchStoredOnly} " +
                        $"owned={Cfg.SearchOwnedEstates} guest={Cfg.SearchGuestEstates} " +
                        $"company={Cfg.SearchCompanyEstates}");
        log.Information($"[FMF] last result: {cachedHits.Count} shown, {cachedHidden} hidden as " +
                        $"placed, {cachedOutOfScope} out of scope");
        log.Information("[FMF] unticked: " + (Cfg.SearchExcluded.Count == 0
            ? "(none)"
            : string.Join(", ", Cfg.SearchExcluded.Select(x => $"\"{Plain(x)}\""))));

        var companyEstates = SearchableCompanyEstates();

        void Line(string kind, string id, string owner, bool searched, List<Piece> pieces)
        {
            var matches = needle.Length == 0
                ? new List<Piece>()
                : pieces.Where(x => x.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();

            log.Information($"[FMF]   {kind} {Plain(id)} owner=\"{Plain(owner)}\" " +
                            $"searched={searched} pieces={pieces.Count} matches={matches.Count}");

            // Spelled out character by character, so a name that only looks like it
            // contains the search text shows what it really holds.
            foreach (var x in (matches.Count > 0 ? matches : pieces).Take(4))
                log.Information($"[FMF]       \"{Plain(x.Name)}\" where={x.Where}");
        }

        foreach (var p in Cfg.Properties.Values)
            Line("property", p.Key.Id, p.Owner, Searchable(p, companyEstates), p.Pieces);

        foreach (var h in Cfg.Holdings.Values)
            Line("holding", h.Id, h.Owner, Searchable(h), h.Pieces);
    }

    public void SearchFor(string text)
    {
        search = text.Length > 100 ? text[..100] : text;
        selectSearch = true;
        IsOpen = true;
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##fmftabs"))
        {
            // Only on the frame it was asked for: left set, the tab would snap back to
            // Search every time another one was clicked.
            var searchFlags = selectSearch ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            selectSearch = false;

            if (ImGui.BeginTabItem("Search", searchFlags)) { DrawSearch(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Furnishings")) { DrawPlaces(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Saved Lists")) { DrawSavedLists(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Settings")) { DrawSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Removal")) { DrawRemoval(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Instructions")) { DrawHelp(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    // ---------- search ----------

    private void DrawSearch()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##q", "Search every indexed furnishing...", ref search, 100);

        var searching = !string.IsNullOrWhiteSpace(search);

        // Cached, because this runs on every frame the window is open. Rebuilding it from
        // every piece in every property sixty times a second - twice over, since the
        // hidden count used to be a second full search - was the window's whole cost.
        Refresh(searching ? search : string.Empty);

        var hits = searching ? cachedHits : null;
        var hidden = searching ? cachedHidden : 0;

        // Directly under the box: what is in the index, or what the search found.
        if (!searching)
        {
            var places = Cfg.Properties.Count;
            var pieces = Cfg.Properties.Values.Sum(p => p.Pieces.Count)
                       + Cfg.Holdings.Values.Sum(h => h.Pieces.Count);

            ImGui.TextWrapped($"{pieces:N0} furnishings indexed across {places} " +
                              $"{(places == 1 ? "property" : "properties")} and " +
                              $"{Cfg.Holdings.Count} other " +
                              $"{(Cfg.Holdings.Count == 1 ? "container" : "containers")}.");
            ImGui.TextDisabled("Type above to search for furnishings across all recorded estates " +
                               "and containers.");
        }
        else
        {
            var line = $"{hits!.Count:N0} {(hits.Count == 1 ? "match" : "matches")}.";

            if (hidden > 0)
            {
                ImGui.TextColored(Warn,
                    line + $" {hidden:N0} placed {(hidden == 1 ? "furnishing is" : "furnishings are")} " +
                           "hidden by \"View stored items only\".");
            }
            else
            {
                ImGui.TextDisabled(line);
            }
        }

        ImGui.Spacing();

        var storedOnly = Cfg.SearchStoredOnly;
        if (ImGui.Checkbox("View stored items only", ref storedOnly))
        {
            Cfg.SearchStoredOnly = storedOnly;
            Cfg.Save(Plugin.PluginInterface);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hides anything already placed, leaving what is in a storeroom, " +
                             "your bags or a retainer.");

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Sort by");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("##sort", SortNames[Sort]))
        {
            for (var i = 0; i < SortNames.Length; i++)
            {
                if (!ImGui.Selectable(SortNames[i], i == Sort)) continue;

                Cfg.SearchSort = i;
                Cfg.Save(Plugin.PluginInterface);
            }

            ImGui.EndCombo();
        }

        DrawSearchFilters();

        ImGui.Spacing();
        DrawRemembered();

        if (!searching) return;

        // Said whether or not anything else matched: the copy someone is looking for may
        // be the one in a character they unticked last week.
        if (cachedOutOfScope > 0)
            ImGui.TextColored(Warn,
                $"{cachedOutOfScope:N0} more {(cachedOutOfScope == 1 ? "match is" : "matches are")} " +
                "in a character or estate type that is unticked under \"Search in\".");

        if (hits!.Count == 0)
        {
            if (hidden > 0)
            {
                ImGui.TextColored(Warn,
                    $"All {hidden:N0} of them are placed. Untick \"View stored items only\" to see where.");
            }
            else
            {
                ImGui.TextDisabled(Cfg.SearchStoredOnly
                    ? "Nothing stored matching that, and nothing placed either."
                    : "Nothing matching, in anywhere visited so far.");
            }

            return;
        }

        DrawTable("##hits", hits, showLocation: true);
    }

    /// <summary>
    /// Case-insensitive substring match over every snapshot. Exact prefix matches sort
    /// first, so searching "bed" puts beds above "Riviera Bedside Table".
    /// </summary>
    private List<Hit> cachedHits = new();
    private int cachedHidden;

    /// <summary>Matches in a character or kind of estate that "Search in" has switched off.</summary>
    private int cachedOutOfScope;
    private string cachedQuery = "\u0000";
    private bool cachedFilter;
    private int cachedRevision = -1;

    /// <summary>
    /// Rebuilds the search results, but only when the question or the answer could have
    /// changed: a different query, a different filter, or an index that has been written
    /// to since. Otherwise the previous results stand and the frame costs nothing.
    /// </summary>
    private const int SortName = 0;
    private const int SortStoredFirst = 1;
    private const int SortInventoriesFirst = 2;
    private const int SortQuantityHigh = 3;
    private const int SortQuantityLow = 4;

    private static readonly string[] SortNames =
    [
        "Item Name",
        "Stored Items First",
        "Inventories First",
        "Quantity (High -> Low)",
        "Quantity (Low -> High)",
    ];

    /// <summary>The chosen order, or item name if the saved number means nothing here.</summary>
    private int Sort => Cfg.SearchSort >= 0 && Cfg.SearchSort < SortNames.Length ? Cfg.SearchSort : SortName;

    private void Refresh(string q)
    {
        if (cachedQuery == q && cachedFilter == Cfg.SearchStoredOnly
            && cachedRevision == Configuration.Revision)
            return;

        cachedQuery = q;
        cachedFilter = Cfg.SearchStoredOnly;
        cachedRevision = Configuration.Revision;

        cachedOutOfScope = 0;
        cachedHits = q.Length == 0 ? new List<Hit>() : Find(q, out cachedHidden, out cachedOutOfScope);
        if (q.Length == 0) cachedHidden = 0;
    }

    /// <param name="hiddenByFilter">
    /// How many matches "View stored items only" held back. Counted on the way past
    /// rather than by searching a second time - a search that finds three things and
    /// shows none of them looks exactly like a search that found nothing, so the number
    /// is needed, but it is not worth a second walk of the whole index for.
    /// </param>
    /// <param name="outOfScope">
    /// How many matches sit somewhere "Search in" has switched off. Counted for the same
    /// reason: an unticked character with the only copy looks exactly like owning none.
    /// </param>
    private List<Hit> Find(string q, out int hiddenByFilter, out int outOfScope)
    {
        var skipped = 0;

        var needle = q.Trim();
        var hits = new List<Hit>();
        var hiding = Cfg.SearchStoredOnly;
        var hidden = 0;

        void Consider(string source, string label, DateTime taken, bool partial, Piece piece,
                      bool inScope, PlaceKind kind)
        {
            if (!piece.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)) return;

            if (!inScope)
            {
                skipped++;
                return;
            }

            if (hiding && SpotNames.IsPlaced(piece.Where))
            {
                hidden++;
                return;
            }

            hits.Add(new Hit(source, label, taken, partial, piece, kind));
        }

        var companyEstates = SearchableCompanyEstates();

        foreach (var p in Cfg.Properties.Values)
        {
            var inScope = Searchable(p, companyEstates);

            var source = "p:" + p.Key.Id;
            foreach (var piece in p.Pieces)
                Consider(source, p.Label, p.TakenUtc, p.Partial, piece, inScope, KindOf(p));
        }

        foreach (var h in Cfg.Holdings.Values)
        {
            var inScope = Searchable(h);

            var source = "h:" + h.Id;
            foreach (var piece in h.Pieces)
                Consider(source, h.DisplayName, h.TakenUtc, h.Partial, piece, inScope, KindOf(h));
        }

        hiddenByFilter = hidden;
        outOfScope = skipped;

        // Storerooms on top, or bags, saddlebag and retainers on top; by name within
        // each. The two quantity orders are applied when the table is drawn instead,
        // because a row's count only exists once identical pieces have been stacked.
        if (Sort is SortStoredFirst or SortInventoriesFirst)
        {
            var storedFirst = Sort == SortStoredFirst;

            return hits
                .OrderByDescending(h => storedFirst
                    ? IsStored(h.Piece.Where)
                    : h.Kind is PlaceKind.Carried or PlaceKind.Retainer)
                .ThenBy(h => h.Piece.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.Location, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return hits
            .OrderByDescending(h => h.Piece.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            .ThenBy(h => h.Piece.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Location, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------- what a search looks through ----------

    /// <summary>True when any character or kind of estate is switched off.</summary>
    private bool SearchIsNarrowed =>
        Cfg.SearchExcluded.Count > 0 || !Cfg.SearchOwnedEstates
        || !Cfg.SearchGuestEstates || !Cfg.SearchCompanyEstates;

    private List<string> searchCharacters = new();
    private int searchCharactersRevision = -1;

    /// <summary>The characters with anything recorded, in the order the list shows them.</summary>
    private List<string> SearchCharacters()
    {
        if (searchCharactersRevision == Configuration.Revision) return searchCharacters;

        searchCharactersRevision = Configuration.Revision;
        searchCharacters = Sections(out _).Select(s => s.Who).ToList();
        return searchCharacters;
    }

    /// <summary>
    /// Tick boxes for whose things, and which kinds of estate, a search looks through.
    ///
    /// Three kinds of estate, split the way ownership splits them rather than the way the
    /// game's menus do: what is yours (a private estate, an apartment, your Private
    /// Chambers), what you are a guest in, and what belongs to the company. The chambers
    /// are in the company's house but they are your room, so they go with what is yours.
    /// </summary>
    private void DrawSearchFilters()
    {
        ImGui.Spacing();
        ImGui.TextDisabled("Search in:");

        var people = SearchCharacters();

        if (people.Count > 0) ImGui.TextUnformatted("Character");

        foreach (var who in people)
        {
            Flow(who);

            var on = !Cfg.SearchExcluded.Contains(who);
            if (ImGui.Checkbox($"{who}###searchwho{who}", ref on))
            {
                if (on) Cfg.SearchExcluded.Remove(who);
                else Cfg.SearchExcluded.Add(who);

                Cfg.Save(Plugin.PluginInterface);
            }

            ImGui.SameLine();
        }

        if (people.Count > 0) ImGui.NewLine();

        ImGui.TextUnformatted("Estate Type");

        var owned = Cfg.SearchOwnedEstates;
        if (ImGui.Checkbox("Owned Estates", ref owned))
        {
            Cfg.SearchOwnedEstates = owned;
            Cfg.Save(Plugin.PluginInterface);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Private estates, apartments and Private Chambers.");

        ImGui.SameLine();
        Flow("Guest Estates");

        var guest = Cfg.SearchGuestEstates;
        if (ImGui.Checkbox("Guest Estates", ref guest))
        {
            Cfg.SearchGuestEstates = guest;
            Cfg.Save(Plugin.PluginInterface);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Estates you can edit but do not own.");

        ImGui.SameLine();
        Flow("Free Company Estate");

        var company = Cfg.SearchCompanyEstates;
        if (ImGui.Checkbox("Free Company Estate", ref company))
        {
            Cfg.SearchCompanyEstates = company;
            Cfg.Save(Plugin.PluginInterface);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The Estate Hall and its yard. Private Chambers count as Owned Estates.");
    }

    /// <summary>
    /// Starts a new line if the next tick box would not fit on this one. Called with the
    /// cursor already moved along the line by SameLine, or at the start of one.
    /// </summary>
    private static void Flow(string label)
    {
        var needed = ImGui.CalcTextSize(label).X + ImGui.GetFrameHeight() + 12f;
        if (ImGui.GetContentRegionAvail().X < needed) ImGui.NewLine();
    }

    /// <summary>
    /// The company estates a search may look in, by property id.
    ///
    /// A hall belongs to no single character, so a character's tick box cannot be read
    /// straight off it. It is searched when somebody ticked belongs to its company - the
    /// same matching that decides whose section it is listed under - and a hall nobody
    /// here belongs to is searched whenever company estates are on at all.
    /// </summary>
    private HashSet<string> SearchableCompanyEstates()
    {
        var ids = new HashSet<string>();
        if (!Cfg.SearchCompanyEstates) return ids;

        var sections = Sections(out var orphans);

        foreach (var s in sections)
        {
            if (Cfg.SearchExcluded.Contains(s.Who)) continue;

            foreach (var p in s.CompanyRows)
                if (p.Estate == EstateType.FreeCompanyEstate) ids.Add(p.Key.Id);
        }

        foreach (var p in orphans) ids.Add(p.Key.Id);
        return ids;
    }

    private bool Searchable(PropertySnapshot p, HashSet<string> companyEstates)
    {
        if (p.Estate == EstateType.FreeCompanyEstate) return companyEstates.Contains(p.Key.Id);

        if (IsGuest(p) ? !Cfg.SearchGuestEstates : !Cfg.SearchOwnedEstates) return false;

        return !Cfg.SearchExcluded.Contains(Who(p.Owner));
    }

    private bool Searchable(HoldingSnapshot h) => !Cfg.SearchExcluded.Contains(Who(h.Owner));

    private readonly List<(string? Location, string Who, PlaceKind Kind, int Count)> remembered = new();
    private readonly List<int> rememberedOrder = new();
    private int rememberedRevision = -1;

    /// <summary>
    /// Where each remembered line is now and how many are there, in one walk of the index
    /// rather than one per line per frame. A line whose place or piece has gone keeps a
    /// null location, and says so on screen.
    ///
    /// Not narrowed by "Search in" or "View stored items only": a line is on this list
    /// because it was ticked by hand, and a filter changed afterwards should not hide it.
    /// </summary>
    private List<(string? Location, string Who, PlaceKind Kind, int Count)> Remembered()
    {
        if (rememberedRevision == Configuration.Revision) return remembered;

        rememberedRevision = Configuration.Revision;
        remembered.Clear();
        rememberedOrder.Clear();

        var lines = Cfg.WatchedLines;
        for (var i = 0; i < lines.Count; i++) remembered.Add((null, string.Empty, PlaceKind.Carried, 0));
        if (lines.Count == 0) return remembered;

        foreach (var p in Cfg.Properties.Values)
            Tally("p:" + p.Key.Id, p.Label, WhoseLine(p.Owner, p.Label), KindOf(p), p.Pieces);

        foreach (var h in Cfg.Holdings.Values)
            Tally("h:" + h.Id, h.DisplayName, WhoseLine(h.Owner, h.DisplayName), KindOf(h), h.Pieces);

        // Lines for the same item sit together, items in the order they were first ticked.
        var firstSeen = new Dictionary<uint, int>();
        for (var i = 0; i < lines.Count; i++) firstSeen.TryAdd(lines[i].ItemId, i);

        rememberedOrder.AddRange(Enumerable.Range(0, lines.Count)
                                           .OrderBy(i => firstSeen[lines[i].ItemId])
                                           .ThenBy(i => i));

        return remembered;
    }

    private void Tally(string source, string label, string who, PlaceKind kind,
                       IEnumerable<Piece> pieces)
    {
        var wanted = false;
        foreach (var w in Cfg.WatchedLines)
            if (w.Source == source) { wanted = true; break; }

        if (!wanted) return;

        foreach (var piece in pieces)
        {
            var at = Cfg.FindWatched(source, piece);
            if (at < 0) continue;

            remembered[at] = (label, who, kind, remembered[at].Count + Math.Max(1, piece.Quantity));
        }
    }

    /// <summary>
    /// The shopping list. Tick lines anywhere in the plugin and they collect here, so a
    /// run to fetch six things is one list rather than six searches and a memory test.
    /// </summary>
    private void DrawRemembered()
    {
        if (Cfg.WatchedLines.Count == 0) return;

        var header = $"Remembered ({Cfg.WatchedLines.Count})###remembered";
        if (ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            ImGui.TextDisabled("Ticking the checkbox records that line's location to a remembered " +
                               "list. Useful if you're gathering multiple items in one trip.");

            var clear = ImGui.Button("Clear All");

            ImGui.SameLine();
            if (ImGui.Button("Save as List"))
            {
                var made = Cfg.SaveWatchedAsList();
                if (made != null)
                {
                    Cfg.Save(Plugin.PluginInterface);
                    savedListName = made.Name;
                    savedListUntil = DateTime.UtcNow.AddSeconds(5);
                }
            }

            if (DateTime.UtcNow < savedListUntil)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"Saved as \"{savedListName}\" on the Saved Lists tab.");
            }

            ImGui.Spacing();

            var found = Remembered();
            var lines = Cfg.WatchedLines;
            var forget = -1;
            uint heading = 0;

            // The two lists are rebuilt together, so they only differ in length on the
            // frame something was ticked elsewhere and not yet saved. Skip that frame.
            if (found.Count == lines.Count)
            {
                foreach (var i in rememberedOrder)
                {
                    var w = lines[i];

                    if (w.ItemId != heading)
                    {
                        if (heading != 0) ImGui.Spacing();
                        heading = w.ItemId;

                        Icons.Draw(w.IconId, IconSize);
                        ImGui.SameLine();
                        ImGui.TextUnformatted(string.IsNullOrEmpty(w.Name) ? $"Item {w.ItemId}" : w.Name);
                    }

                    ImGui.Indent();

                    if (ImGui.SmallButton($"Forget###unwatch{i}")) forget = i;
                    ImGui.SameLine();

                    var (location, who, kind, count) = found[i];
                    var spot = SpotNames.Label(w.Where) +
                               (string.IsNullOrEmpty(w.Detail) ? string.Empty : $" ({w.Detail})");

                    if (location == null)
                    {
                        // Moved, taken out, or the place was removed since it was ticked.
                        ImGui.TextDisabled($"{spot} - no longer recorded there.");
                    }
                    else
                    {
                        // Plain "x" rather than the multiplication sign: the font atlas is
                        // not guaranteed to carry it, and a missing glyph box would look
                        // like a bug in the very list meant to be easier to read.
                        var text = $"{location} - {spot}" + (count > 1 ? $"  x{count:N0}" : string.Empty);

                        Colored(Tint(IsStored(w.Where), kind), text);

                        Whose(who);
                    }

                    ImGui.Unindent();
                }
            }

            if (clear)
            {
                lines.Clear();
                Cfg.Save(Plugin.PluginInterface);
            }
            else if (forget >= 0 && forget < lines.Count)
            {
                lines.RemoveAt(forget);
                Cfg.Save(Plugin.PluginInterface);
            }

            ImGui.Unindent();
        }

        ImGui.Separator();
    }

    // ---------- saved lists ----------

    private DateTime savedListUntil = DateTime.MinValue;
    private string savedListName = string.Empty;

    /// <summary>The list whose Delete has been pressed once and is waiting for the second.</summary>
    private string deleteListId = string.Empty;

    private readonly Dictionary<uint, List<(string Text, string Who, bool Stored, PlaceKind Kind, int Count)>> savedWhere = new();
    private int savedRevision = -1;

    /// <summary>
    /// Where every copy of every saved furnishing is right now, in one walk of the index.
    /// Read from the current records each time they change, which is the whole point: a
    /// saved list names furnishings, and this is what says where they are today.
    /// </summary>
    private Dictionary<uint, List<(string Text, string Who, bool Stored, PlaceKind Kind, int Count)>> SavedWhere()
    {
        if (savedRevision == Configuration.Revision) return savedWhere;

        savedRevision = Configuration.Revision;
        savedWhere.Clear();

        foreach (var list in Cfg.SavedLists)
            foreach (var item in list.Items)
                savedWhere.TryAdd(item.ItemId, new List<(string, string, bool, PlaceKind, int)>());

        if (savedWhere.Count == 0) return savedWhere;

        void Add(string label, string who, PlaceKind kind, IEnumerable<Piece> pieces)
        {
            foreach (var piece in pieces)
            {
                if (!savedWhere.TryGetValue(piece.ItemId, out var into)) continue;

                var stored = IsStored(piece.Where);
                var text = $"{label} - {SpotNames.Label(piece.Where)}" +
                           (string.IsNullOrEmpty(piece.Detail) ? string.Empty : $" ({piece.Detail})");
                var n = Math.Max(1, piece.Quantity);

                var at = into.FindIndex(l => l.Text == text && l.Who == who && l.Stored == stored);
                if (at >= 0) into[at] = (text, who, stored, kind, into[at].Count + n);
                else into.Add((text, who, stored, kind, n));
            }
        }

        foreach (var p in Cfg.Properties.Values)
            Add(p.Label, WhoseLine(p.Owner, p.Label), KindOf(p), p.Pieces);

        foreach (var h in Cfg.Holdings.Values)
            Add(h.DisplayName, WhoseLine(h.Owner, h.DisplayName), KindOf(h), h.Pieces);

        // By character first, so one character's places sit together under each item.
        foreach (var lines in savedWhere.Values)
            lines.Sort((a, b) =>
            {
                var byWho = string.Compare(a.Who, b.Who, StringComparison.OrdinalIgnoreCase);
                return byWho != 0 ? byWho
                    : string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);
            });

        return savedWhere;
    }

    /// <summary>
    /// The character a line belongs to, or nothing when the place is already named for
    /// them - "Sarna Umbra's inventory (Cactuar)" does not need telling twice.
    /// </summary>
    private static string WhoseLine(string owner, string label)
    {
        if (string.IsNullOrWhiteSpace(owner)) return string.Empty;

        var bare = CharacterName.Bare(owner);
        return bare.Length > 0 && label.Contains(bare, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : owner;
    }

    /// <summary>Whose it is, dimmed, on the end of the line just drawn.</summary>
    private static void Whose(string who)
    {
        if (who.Length == 0) return;

        ImGui.SameLine();
        ImGui.TextDisabled($"- {who}");
    }

    private void DrawSavedLists()
    {
        if (Cfg.SavedLists.Count == 0)
        {
            ImGui.TextWrapped("No saved lists yet. Tick furnishings on the Search tab, then press " +
                              "Save as List on the Remembered list.");
            return;
        }

        var where = SavedWhere();
        SavedList? delete = null;

        foreach (var list in Cfg.SavedLists)
        {
            var count = list.Items.Count;
            var title = string.IsNullOrWhiteSpace(list.Name) ? "Unnamed list" : list.Name;

            // The id after ### is the list's own, so the header stays open while its name
            // is being typed over.
            if (!ImGui.CollapsingHeader($"{title} ({count:N0} furnishing{(count == 1 ? "" : "s")})###saved{list.Id}"))
                continue;

            ImGui.Indent();

            var name = list.Name;
            ImGui.SetNextItemWidth(260);
            if (ImGui.InputTextWithHint($"##savedname{list.Id}", "Name this list...", ref name, 60))
                list.Name = name;

            // Written when you finish typing, not on every key.
            if (ImGui.IsItemDeactivatedAfterEdit())
                Cfg.Save(Plugin.PluginInterface);

            ImGui.SameLine();

            // Two presses, because a list is something you built by hand and there is no
            // getting it back.
            if (deleteListId == list.Id)
            {
                if (ImGui.Button($"Confirm delete###saveddel{list.Id}")) delete = list;
                ImGui.SameLine();
                if (ImGui.Button($"Keep###savedkeep{list.Id}")) deleteListId = string.Empty;
            }
            else if (ImGui.Button($"Delete list###saveddel{list.Id}"))
            {
                deleteListId = list.Id;
            }

            ImGui.Spacing();

            SavedItem? remove = null;

            foreach (var item in list.Items)
            {
                if (ImGui.SmallButton($"Remove###savedrm{list.Id}{item.ItemId}")) remove = item;
                ImGui.SameLine();
                Icons.Draw(item.IconId, IconSize);
                ImGui.SameLine();
                ImGui.TextUnformatted(string.IsNullOrEmpty(item.Name) ? $"Item {item.ItemId}" : item.Name);

                ImGui.Indent();

                if (!where.TryGetValue(item.ItemId, out var lines) || lines.Count == 0)
                {
                    ImGui.TextDisabled("Not recorded anywhere right now.");
                }
                else
                {
                    foreach (var (text, who, stored, kind, n) in lines)
                    {
                        // Plain "x" rather than the multiplication sign: the font atlas
                        // is not guaranteed to carry it.
                        var label = n > 1 ? $"{text}  x{n:N0}" : text;

                        Colored(Tint(stored, kind), label);

                        Whose(who);
                    }
                }

                ImGui.Unindent();
                ImGui.Spacing();
            }

            if (remove != null)
            {
                list.Items.Remove(remove);
                Cfg.Save(Plugin.PluginInterface);
            }

            ImGui.Unindent();
        }

        if (delete != null)
        {
            Cfg.SavedLists.Remove(delete);
            deleteListId = string.Empty;
            Cfg.Save(Plugin.PluginInterface);
        }
    }

    // ---------- places ----------

    private static readonly Vector4 Heading = new(0.85f, 0.78f, 0.55f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.75f, 0.25f, 1f);

    private static PlaceKind KindOf(PropertySnapshot p) =>
        IsGuest(p) ? PlaceKind.Guest
        : p.Estate == EstateType.FreeCompanyEstate ? PlaceKind.Company
        : PlaceKind.Owned;

    private static PlaceKind KindOf(HoldingSnapshot h) =>
        h.Section == Section.Retainers ? PlaceKind.Retainer : PlaceKind.Carried;

    private static bool IsStored(Spot where) => where is Spot.IndoorStoreroom or Spot.OutdoorStoreroom;

    /// <summary>
    /// The colour a row is written in, from Settings. A stored piece takes the storeroom
    /// colour whatever kind of estate it is in - "is it out or put away" is the question
    /// the tint was first added to answer - and everything else goes by where it is.
    /// </summary>
    private Vector4? Tint(bool stored, PlaceKind kind)
    {
        var c = stored ? Cfg.ColorStoreroom : kind switch
        {
            PlaceKind.Retainer => Cfg.ColorRetainers,
            PlaceKind.Owned => Cfg.ColorOwned,
            PlaceKind.Company => Cfg.ColorCompany,
            PlaceKind.Guest => Cfg.ColorGuest,
            _ => Cfg.ColorInventory,
        };

        return new Vector4(c.R, c.G, c.B, 1f);
    }

    private static void Colored(Vector4? tint, string text)
    {
        if (tint is { } colour) ImGui.TextColored(colour, text);
        else ImGui.TextUnformatted(text);
    }

    private void DrawPlaces()
    {
        if (Cfg.Properties.Count == 0 && Cfg.Holdings.Count == 0)
        {
            ImGui.TextWrapped("Nothing indexed yet. Enter a house, apartment or Free Company " +
                              "chamber you have editing rights to and it will be recorded.");
            return;
        }

        // The same filter the Search tab offers, so the toggle means one thing wherever
        // you meet it.
        var storedOnly = Cfg.SearchStoredOnly;
        if (ImGui.Checkbox("View stored items only", ref storedOnly))
        {
            Cfg.SearchStoredOnly = storedOnly;
            Cfg.Save(Plugin.PluginInterface);
        }

        ImGui.Spacing();

        // Taken first, not inside the loop header: a variable declared there belongs to
        // the loop and is gone by the time the leftover company property is drawn below.
        var sections = Sections(out var orphans);

        foreach (var s in sections)
        {
            // The character you are on opens by default. Search reads everything whatever
            // is folded, so collapsing hides nothing from the index.
            var flags = s.IsMe ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;

            if (!ImGui.CollapsingHeader($"{s.Who} ({s.Total:N0})###who{s.Who}", flags)) continue;

            ImGui.Indent();

            // What the character is carrying themselves, named first and without a heading
            // of its own - the bags and saddlebag are the character.
            foreach (var h in s.Carried) DrawHolding(h);

            Group("Retainers", s.Retainers, DrawHolding);
            Group("Owned Estates", s.Owned, DrawProperty);
            Group("Guest Estates", s.Guests, DrawProperty);
            Group(CompanyHeading(s.Who), s.CompanyRows, DrawProperty);

            ImGui.Unindent();
            ImGui.Spacing();
        }

        // Anything whose company no character here belongs to.
        foreach (var group in orphans.GroupBy(p => p.FreeCompany)
                                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ToList();
            var sum = rows.Sum(p => p.Pieces.Count);

            if (!ImGui.CollapsingHeader($"Free Company ({sum:N0})###fc{group.Key}",
                                        ImGuiTreeNodeFlags.DefaultOpen)) continue;

            ImGui.Indent();
            foreach (var p in rows) DrawProperty(p);
            ImGui.Unindent();
            ImGui.Spacing();
        }
    }

    /// <summary>One character's part of the list, already sorted into its groups.</summary>
    private sealed record CharacterSection(
        string Who,
        bool IsMe,
        int Total,
        List<HoldingSnapshot> Carried,
        List<HoldingSnapshot> Retainers,
        List<PropertySnapshot> Owned,
        List<PropertySnapshot> Guests,
        string CompanyName,
        List<PropertySnapshot> CompanyRows);

    /// <summary>
    /// Everything indexed, arranged the way it is shown: by character, then by kind.
    ///
    /// One arrangement, used by both the Furnishings tab and the Removal tab, so the two
    /// can never disagree about where something belongs.
    ///
    /// Grouped by character rather than by kind. With alts, "Apartment" and "someone's
    /// inventory" repeated down the list with no way to tell whose was whose.
    ///
    /// Company property sits inside the sections of the characters who belong to that
    /// company - matched by company, never by who happened to record it. An alt in another
    /// company does not get somebody else's hall in their section, and a hall whose
    /// company nobody here belongs to comes back in <paramref name="orphans"/> rather than
    /// being filed under a guess.
    /// </summary>
    private List<CharacterSection> Sections(out List<PropertySnapshot> orphans)
    {
        var me = Me;

        // Company property - the hall and its yard - belongs to the company rather than to
        // any one character, so it is held back here and placed inside the sections of the
        // characters who belong to that company.
        var fcProps = Cfg.Properties.Values
            .Where(p => p.Estate == EstateType.FreeCompanyEstate)
            .OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var props = Cfg.Properties.Values.Except(fcProps).ToList();
        var holds = Cfg.Holdings.Values.ToList();

        var characters = props.Select(p => Who(p.Owner))
            .Concat(holds.Select(h => Who(h.Owner)))
            .Concat(Cfg.CompanyOf.Keys)
            .Distinct()
            .OrderByDescending(n => n == me)
            .ThenBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var claimed = new HashSet<string>();
        var sections = new List<CharacterSection>();

        foreach (var who in characters)
        {
            var company = Cfg.CompanyOf.GetValueOrDefault(who, string.Empty);

            var mine = props.Where(p => Who(p.Owner) == who)
                            .OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ToList();
            var held = holds.Where(h => Who(h.Owner) == who)
                            .OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            // Estates you have editing rights to but do not own. Kept apart from your own,
            // because somebody else can empty one between visits and a count you cannot
            // account for is worse than no count.
            var guests = mine.Where(IsGuest).ToList();

            // A chamber is the character's own room, but it is a room in the company's
            // house - so it is listed with the company's property rather than beside an
            // apartment and a private estate, which it has nothing else in common with.
            var chambers = mine.Where(IsChamber).ToList();
            var owned = mine.Except(guests).Except(chambers).ToList();

            var retainers = held.Where(h => h.Section == Section.Retainers).ToList();
            var carried = held.Except(retainers).ToList();

            var theirFcProps = company.Length == 0
                ? new List<PropertySnapshot>()
                // By world as well as by name. Company names are only unique within a
                // world, so two alts in different companies that happen to share a name
                // would otherwise each be shown the other's hall. A hall is recorded by
                // someone on its own world, which is what its owner's world stands for.
                : fcProps.Where(p => p.FreeCompany == company
                                     && CharacterName.SameWorld(p.Owner, who)).ToList();

            foreach (var p in theirFcProps) claimed.Add(p.Key.Id);

            var total = mine.Sum(p => p.Pieces.Count) + held.Sum(h => h.Pieces.Count)
                      + theirFcProps.Sum(p => p.Pieces.Count);

            // Named for the character's company, or failing that for the one the chamber
            // itself was recorded under - a character not seen since company membership
            // was tracked still has a chamber that knows whose house it is in.
            var companyName = company.Length > 0
                ? company
                : chambers.Select(c => c.FreeCompany)
                          .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty;

            sections.Add(new CharacterSection(
                who, who == me, total, carried, retainers, owned, guests, companyName,
                theirFcProps.Concat(chambers).ToList()));
        }

        orphans = fcProps.Where(p => !claimed.Contains(p.Key.Id)).ToList();
        return sections;
    }

    /// <summary>
    /// A labelled run of rows inside a character's section. Drawn only when it has
    /// something in it - an empty "Guest Estates" is a question the window cannot answer.
    /// </summary>
    private void Group<T>(string title, List<T> rows, Action<T> draw)
    {
        if (rows.Count == 0) return;

        ImGui.TextColored(Heading, title);
        foreach (var row in rows) draw(row);
    }

    /// <summary>
    /// The heading over a character's company property - "Free Company (Sarna Umbra)".
    /// Named for the character whose company it is, never for the company itself.
    /// </summary>
    private static string CompanyHeading(string who) =>
        string.IsNullOrWhiteSpace(who) || who == "Unknown"
            ? "Free Company"
            : $"Free Company ({CharacterName.Bare(who)})";

    /// <summary>
    /// Whether a property is somebody else's that you merely have the run of.
    ///
    /// Only the teleport list can say so, and it only says so for estates explicitly
    /// shared with you. An estate it has nothing to say about is treated as your own,
    /// which is what it was before guest estates had a section at all.
    /// </summary>
    private static bool IsGuest(PropertySnapshot p) => p.Estate == EstateType.SharedEstate;

    /// <summary>
    /// A Free Company chamber: identified as one by the game, or, where the game had
    /// nothing to say, a numbered room that is not an apartment.
    /// </summary>
    private static bool IsChamber(PropertySnapshot p) =>
        p.Estate == EstateType.PersonalChambers
        || (p.Estate == null && p.Key.Room > 0 && !p.Key.IsApartment);

    /// <summary>A character's name, or Unknown for anything recorded before names were kept.</summary>
    private static string Who(string owner) =>
        string.IsNullOrWhiteSpace(owner) ? "Unknown" : owner;

    /// <summary>
    /// The pieces a list should show, after the stored-only filter.
    /// </summary>
    private List<Piece> Visible(List<Piece> pieces) =>
        Cfg.SearchStoredOnly
            ? pieces.Where(x => !SpotNames.IsPlaced(x.Where)).ToList()
            : pieces;

    /// <summary>
    /// A property header that explains its own number: how much is out in the room and how
    /// much is in the storeroom. The same wording for every kind of property, and no
    /// maximum - the game's own Furnishings window is the place for that.
    /// </summary>
    private static string Breakdown(PropertySnapshot p)
    {
        var placed = p.Pieces.Count(x => SpotNames.IsPlaced(x.Where));
        var stored = p.Pieces.Count - placed;

        return $"{placed:N0} placed, {stored:N0} stored";
    }

    private void DrawProperty(PropertySnapshot p)
    {
        var shown = Visible(p.Pieces);

        if (!ImGui.CollapsingHeader($"{p.Label} ({Breakdown(p)})###prop{p.Key.Id}"))
            return;

        ImGui.Indent();

        // The address is always shown, never just on a rename: several properties can
        // share a name - two apartments are both "Apartment" - and this is what tells you
        // which one you are looking at.
        if (p.Label != p.Address) ImGui.TextDisabled(p.Address);
        Provenance(p.TakenUtc, p.Partial);

        // Worth saying loudly: this list is not merely incomplete, it is missing the
        // furniture itself.
        if (!p.PlacedLoaded)
            ImGui.TextColored(Warn,
                              "The furnishings here have not loaded. Open the housing menu while " +
                              "standing at this property and it records itself properly.");

        var name = p.CustomName;
        ImGui.SetNextItemWidth(240);

        // A guest estate is listed by its address because the plugin never reads whose it
        // is. This box is where you say so yourself: whatever is typed here is your own
        // note, kept locally, and it replaces the address as the name everywhere.
        var hint = IsGuest(p) ? "Name this estate..." : "Call it something else...";

        if (ImGui.InputTextWithHint($"##name{p.Key.Id}", hint, ref name, 60))
            p.CustomName = name;

        // Written when you finish typing, not on every key. A save serialises the whole
        // index, and a twelve-letter name used to do that twelve times.
        if (ImGui.IsItemDeactivatedAfterEdit())
            Cfg.Save(Plugin.PluginInterface);

        // There was a section picker here. The list is grouped by character now and reads
        // nothing from a property's section, so the picker changed a value that nothing
        // looked at.

        DrawTable($"##t{p.Key.Id}", Sorted(shown.Select(x =>
            new Hit("p:" + p.Key.Id, p.Label, p.TakenUtc, p.Partial, x, KindOf(p)))), showLocation: false);

        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawHolding(HoldingSnapshot h)
    {
        var shown = Visible(h.Pieces);

        // A container has no placed-versus-stored to explain, so it just says how many -
        // and says what it is counting, because a bare "0 total" on a retainer reads like
        // the retainer was never read rather than like it holds no furniture.
        var held = h.Pieces.Count;
        var label = $"{held:N0} furnishing{(held == 1 ? "" : "s")}";

        if (!ImGui.CollapsingHeader($"{h.DisplayName} ({label})###hold{h.Id}"))
            return;

        ImGui.Indent();
        Provenance(h.TakenUtc, h.Partial);
        DrawTable($"##th{h.Id}", Sorted(shown.Select(x =>
            new Hit("h:" + h.Id, h.DisplayName, h.TakenUtc, h.Partial, x, KindOf(h)))), showLocation: false);
        ImGui.Unindent();
        ImGui.Spacing();
    }

    // ---------- removal ----------

    /// <summary>
    /// Deleting lives in its own tab, away from the lists you browse. Forgetting a
    /// property throws away a record that can only be rebuilt by travelling there.
    /// </summary>
    private void DrawRemoval()
    {
        ImGui.TextWrapped("Forgetting a property discards everything recorded about it. " +
                          "The only way to get it back is to go and stand in it again.");
        ImGui.TextDisabled("Bags and retainers rebuild themselves the next time they are open.");
        ImGui.Spacing();

        if (Cfg.Properties.Count == 0 && Cfg.Holdings.Count == 0)
        {
            ImGui.TextDisabled("Nothing indexed.");
            return;
        }

        ImGui.Separator();

        // Laid out exactly as the Furnishings tab is, from the same arrangement, so the
        // thing you are about to forget is found where you are used to seeing it.
        // Taken first, not inside the loop header: a variable declared there belongs to
        // the loop and is gone by the time the leftover company property is drawn below.
        var sections = Sections(out var orphans);

        foreach (var s in sections)
        {
            var flags = s.IsMe ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;

            if (!ImGui.CollapsingHeader($"{s.Who}###rmwho{s.Who}", flags)) continue;

            ImGui.Indent();

            foreach (var h in s.Carried) ForgetHoldingRow(h);

            Group("Retainers", s.Retainers, ForgetHoldingRow);
            Group("Owned Estates", s.Owned, p => ForgetPropertyRow(p, s.Who));
            Group("Guest Estates", s.Guests, p => ForgetPropertyRow(p, s.Who));
            Group(CompanyHeading(s.Who), s.CompanyRows, p => ForgetPropertyRow(p, s.Who));

            ImGui.Unindent();
            ImGui.Spacing();
        }

        foreach (var group in orphans.GroupBy(p => p.FreeCompany)
                                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!ImGui.CollapsingHeader($"Free Company###rmfc{group.Key}",
                                        ImGuiTreeNodeFlags.DefaultOpen)) continue;

            ImGui.Indent();
            foreach (var row in group.OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList())
                ForgetPropertyRow(row, "fc");
            ImGui.Unindent();
            ImGui.Spacing();
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Forget everything"))
        {
            Cfg.ForgetEverything();
            Cfg.Save(Plugin.PluginInterface);
        }
        ImGui.TextDisabled("Clears every property, bag and retainer above.");
    }

    /// <param name="scope">
    /// Whose section the row is drawn in. Company property appears under every character
    /// in that company, so the button's id needs more than the property to be unique.
    /// </param>
    private void ForgetPropertyRow(PropertySnapshot p, string scope)
    {
        if (ImGui.Button($"Forget###rm{p.Key.Id}_{scope}"))
        {
            Cfg.Forget(p.Key.Id);
            Cfg.Save(Plugin.PluginInterface);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(IsGuest(p) ? $"{p.Label} (Guest Estate)" : p.Label);

        if (p.Label != p.Address)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(p.Address);
        }
    }

    private void ForgetHoldingRow(HoldingSnapshot h)
    {
        if (ImGui.Button($"Forget###rmh{h.Id}"))
        {
            Cfg.Holdings.Remove(h.Id);
            Cfg.Save(Plugin.PluginInterface);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(h.DisplayName);
    }

    /// <summary>
    /// When this was recorded, and whether it was complete. Shown on every list rather
    /// than buried in settings: the plugin is answering from memory, and a reader who does
    /// not know how old the memory is cannot judge the answer.
    /// </summary>
    private void Provenance(DateTime takenUtc, bool partial)
    {
        var local = takenUtc.ToLocalTime();
        var age = DateTime.UtcNow - takenUtc;
        var when = age.TotalMinutes < 90 ? $"{(int)age.TotalMinutes} min ago"
                 : age.TotalHours < 48 ? $"{(int)age.TotalHours} hours ago"
                 : $"{(int)age.TotalDays} days ago";

        if (Cfg.IsStale(takenUtc))
            ImGui.TextColored(Warn,
                              $"Recorded {local:d MMM yyyy, HH:mm} ({when}) - worth revisiting.");
        else
            ImGui.TextDisabled($"Recorded {local:d MMM yyyy, HH:mm} ({when}).");

        if (partial)
            ImGui.TextColored(Warn,
                              "Recorded before the storeroom loaded, so anything stored there is " +
                              "missing. Open the storeroom while standing there and this fills in.");
    }

    /// <summary>Alphabetical by name, so four Wooden Handrails sit together.</summary>
    private static List<Hit> Sorted(IEnumerable<Hit> rows) =>
        rows.OrderBy(h => h.Piece.Name, StringComparer.OrdinalIgnoreCase).ToList();

    // ---------- shared table ----------

    /// <summary>
    /// Rows as they should be drawn, identical ones folded together with a count.
    ///
    /// "Identical" means the same item, in the same spot, with the same dye and detail, in
    /// the same place - anything that would otherwise have printed the same line twice.
    /// The count sums quantities, so a bag holding two stacks of five reads as ten rather
    /// than as two.
    /// </summary>
    private List<(Hit Row, int Count)> Grouped(List<Hit> rows)
    {
        if (!Cfg.StackIdentical)
            return rows.Select(r => (r, Math.Max(1, r.Piece.Quantity))).ToList();

        var grouped = new List<(Hit Row, int Count)>();
        var seen = new Dictionary<string, int>();

        foreach (var row in rows)
        {
            var p = row.Piece;
            var key = $"{p.ItemId}|{(int)p.Where}|{p.Detail}|{p.StainName}|{row.Location}";
            var n = Math.Max(1, p.Quantity);

            if (seen.TryGetValue(key, out var at))
                grouped[at] = (grouped[at].Row, grouped[at].Count + n);
            else
            {
                seen[key] = grouped.Count;
                grouped.Add((row, n));
            }
        }

        return grouped;
    }

    /// <summary>Rows a table may hold before it gets a scroll region of its own.</summary>
    private const int ScrollAfterRows = 25;

    private void DrawTable(string id, List<Hit> rows, bool showLocation)
    {
        var cols = showLocation ? 5 : 4;
        var shown = Grouped(rows);

        // A scrolling table claims the rest of the window whether it needs the room or
        // not, which left a short list - two things in your bags - sitting above a screen
        // of nothing. Only ask for a scroll region once there is enough to scroll; below
        // that the table hugs its rows and the window scrolls instead.
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.SizingStretchProp;

        if (shown.Count > ScrollAfterRows) flags |= ImGuiTableFlags.ScrollY;

        if (!ImGui.BeginTable(id, cols, flags)) return;

        ImGui.TableSetupColumn("##keep", ImGuiTableColumnFlags.WidthFixed, 26);
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, IconSize + 6);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthStretch, 2);
        if (showLocation) ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableHeadersRow();

        // Search results only. Done here rather than in the search itself: the count is
        // the stacked row's, which does not exist until Grouped has run.
        if (showLocation && (Sort is SortQuantityHigh or SortQuantityLow))
        {
            shown = (Sort == SortQuantityHigh
                    ? shown.OrderByDescending(r => r.Count)
                    : shown.OrderBy(r => r.Count))
                .ThenBy(r => r.Row.Piece.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Row.Location, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var index = 0;
        Piece? picked = null;

        // How many of each furnishing the whole list holds, and over how many rows. Only
        // for search results: there the same item turns up in several places, and the
        // sum across them is the number someone is usually after.
        Dictionary<uint, (int Total, int Rows)>? totals = null;

        if (showLocation)
        {
            totals = new Dictionary<uint, (int, int)>();

            foreach (var (row, count) in shown)
            {
                var had = totals.GetValueOrDefault(row.Piece.ItemId);
                totals[row.Piece.ItemId] = (had.Total + count, had.Rows + 1);
            }
        }

        foreach (var (row, count) in shown)
        {
            var p = row.Piece;
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var keep = Cfg.IsWatched(row.Source, p);

            // Numbered by row. Built from the item and its location, two rows could share
            // an id - the same chair placed and stored in one property - and the two boxes
            // then behaved as one.
            if (ImGui.Checkbox($"##keep{index++}", ref keep))
            {
                if (!Cfg.ToggleWatched(row.Source, p))
                {
                    listFullUntil = DateTime.UtcNow.AddSeconds(5);
                    listFullTable = id;
                }
                else
                    Cfg.Save(Plugin.PluginInterface);
            }

            ImGui.TableNextColumn();
            Icons.Draw(p.IconId, IconSize);

            // Tinted so a list can be read at a glance for what is out in the room, what
            // is in the storeroom and whose place it is in. The colours are in Settings.
            var tint = Tint(IsStored(p.Where), row.Kind);

            // Plain "x" rather than the multiplication sign: the font atlas is not
            // guaranteed to carry it, and a missing glyph would look like a fault.
            var label = count > 1 ? $"{p.Name}  x{count:N0}" : p.Name;

            ImGui.TableNextColumn();
            Colored(tint, label);

            // The name is a link: click it for the menu that adds it to a saved list.
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip("Click to add this furnishing to a saved list.");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) picked = p;
            }

            // Only where it adds something: one row already shows its own count.
            if (totals != null && totals.TryGetValue(p.ItemId, out var sum) && sum.Rows > 1)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"- {sum.Total:N0} total");
            }

            ImGui.TableNextColumn();
            var where = string.IsNullOrEmpty(p.Detail)
                ? SpotNames.Label(p.Where)
                : $"{SpotNames.Label(p.Where)} - {p.Detail}";

            Colored(tint, where);

            if (showLocation)
            {
                ImGui.TableNextColumn();
                Colored(tint, row.Location);
                if (row.Partial || Cfg.IsStale(row.TakenUtc))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Warn, "*");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(row.Partial
                            ? "Recorded before the storeroom loaded, so stored items are missing " +
                              "from this property. Open the storeroom while standing there."
                            : "Recorded a while ago. Your own changes re-record themselves, so " +
                              "this only drifts if someone else rearranged the place.");
                }
            }
        }

        ImGui.EndTable();

        // Opened and drawn out here, after the table: a popup has to be opened at the
        // same level it is drawn at, and inside the table is a level further in.
        var popup = "addto" + id;

        if (picked != null)
        {
            addToList = picked;
            ImGui.OpenPopup(popup);
        }

        DrawAddToList(popup);

        // Held on screen for a few seconds. Shown only on the frame of the click, it was
        // gone before it could be read.
        if (listFullTable == id && DateTime.UtcNow < listFullUntil)
            ImGui.TextColored(Warn, $"The remembered list holds {Configuration.WatchLimit} lines. " +
                                    "Clear something from it first.");
    }

    private DateTime listFullUntil = DateTime.MinValue;
    private string listFullTable = string.Empty;

    // ---------- instructions ----------

    private static void Point(string text)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextWrapped(text);
    }

    private void DrawHelp()
    {
        ImGui.TextWrapped("This tool creates a searchable index of your furnishings across all " +
                          "character inventories and estates that you have house editing " +
                          "privileges to.");

        ImGui.Spacing();
        ImGui.TextUnformatted("The following is recordable:");
        ImGui.Spacing();
        Point("Private Estates - indoors and yard (placed items and storeroom)");
        Point("Apartment");
        Point("Private Chambers");
        Point("Free Company estates you have editing access to - indoors and yard");
        Point("Inventory, chocobo saddlebag and retainers");

        ImGui.Spacing();
        ImGui.TextWrapped("Only furnishing items are logged. Guest estates and Free Company " +
                          "estates can be turned off in Settings.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Heading, "Getting started");
        ImGui.Spacing();
        ImGui.TextWrapped("Estates: step inside and open the Furnishings window, then the " +
                          "Storeroom. Each has to be opened once. Do the same in the yard.");
        ImGui.Spacing();
        ImGui.TextWrapped("Inventories: open your inventory, saddlebag or retainer.");
        ImGui.Spacing();
        ImGui.TextWrapped("A message in chat confirms each recording:");
        ImGui.TextDisabled("    [FMF] Private Estate (Owned, Medium): 300 placed, 88 stored.");
        ImGui.TextDisabled("    [FMF] Fishbone (retainer): 33 furnishings recorded.");
        ImGui.Spacing();
        ImGui.TextWrapped("Once everything is recorded, you can turn these messages off in Settings.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Heading, "Remembered list");
        ImGui.Spacing();
        ImGui.TextWrapped("Tick the checkbox next to a line and it stays on the Search tab. " +
                          "Each line is ticked on its own, so you can pick the copies you're " +
                          "going to fetch. Useful if you're gathering multiple items in one " +
                          "trip. Holds up to 50.");
        ImGui.Spacing();
        ImGui.Bullet();
        ImGui.SameLine();
        Colored(Tint(true, PlaceKind.Owned), "Stored items");
        ImGui.SameLine();
        ImGui.TextWrapped("are shown in this color. You can change the colors in Settings.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Heading, "Saved Lists");
        ImGui.Spacing();
        ImGui.TextWrapped("Press Save as List on the Remembered list to keep its furnishings " +
                          "under a name. A saved list always shows where every copy is right " +
                          "now, so it stays current as you move things.");
        ImGui.Spacing();
        ImGui.TextWrapped("To add to a list later, click a furnishing's name anywhere and pick " +
                          "the list.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Heading, "Commands");
        ImGui.Spacing();
        Point("/fmf or /fsearch - open the window");
        Point("/fmf <text> - search");
        Point("/fmf here - record this property now");
        Point("/fmf bags - rescan bags, saddlebag and retainers");
        Point("/fmf dump - write diagnostics to the log, for reporting a problem");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Heading, "Your data");
        ImGui.Spacing();
        ImGui.TextWrapped("Everything is saved locally, in a file on your computer, and is never " +
                          "sent anywhere. You can remove any estate or container under the " +
                          "Removal tab.");
    }

    // ---------- settings ----------

    private void DrawSettings()
    {
        var guest = Cfg.RecordSharedEstates;
        if (ImGui.Checkbox("Record guest estates for which you have housing privileges", ref guest))
        {
            Cfg.RecordSharedEstates = guest;
            Cfg.Save(Plugin.PluginInterface);
        }

        var company = Cfg.RecordCompanyEstates;
        if (ImGui.Checkbox("Record Free Company estates for which you have housing privileges",
                           ref company))
        {
            Cfg.RecordCompanyEstates = company;
            Cfg.Save(Plugin.PluginInterface);
        }

        var say = Cfg.AnnounceCaptures;
        if (ImGui.Checkbox("Confirm each recording in chat", ref say))
        {
            Cfg.AnnounceCaptures = say;
            Cfg.Save(Plugin.PluginInterface);
        }

        var stack = Cfg.StackIdentical;
        if (ImGui.Checkbox("Show identical items as one row with a count", ref stack))
        {
            Cfg.StackIdentical = stack;
            Cfg.Save(Plugin.PluginInterface);
        }

        var stale = Cfg.StaleAfterDays;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Flag snapshots older than (days)", ref stale))
        {
            Cfg.StaleAfterDays = Math.Max(0, stale);
            Cfg.Save(Plugin.PluginInterface);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Heading, "Text colors");
        ImGui.Spacing();

        ColorBox("Storeroom", Cfg.ColorStoreroom);
        ColorBox("Inventory", Cfg.ColorInventory);
        ColorBox("Retainers", Cfg.ColorRetainers);
        ColorBox("Owned Estates", Cfg.ColorOwned);
        ColorBox("Free Company Estate", Cfg.ColorCompany);
        ColorBox("Guest Estates", Cfg.ColorGuest);

        if (ImGui.Button("Reset colors"))
        {
            Cfg.ResetColors();
            Cfg.Save(Plugin.PluginInterface);
        }

        // Written once the picker is let go of, not on every frame of a drag: a save
        // serialises the whole index.
        if (colorsChanged && !ImGui.IsAnyItemActive())
        {
            colorsChanged = false;
            Cfg.Save(Plugin.PluginInterface);
        }

        if (plugin.Demo.On) DrawDemoNames();
    }

    private bool colorsChanged;

    private void ColorBox(string label, TextColor color)
    {
        var value = new Vector3(color.R, color.G, color.B);

        if (!ImGui.ColorEdit3($"{label}###color{label}", ref value, ImGuiColorEditFlags.NoInputs))
            return;

        color.Set(value.X, value.Y, value.Z);
        colorsChanged = true;
    }

    /// <summary>
    /// The stand-in for each name, while screenshot mode is on. The one place the real
    /// names still show, because a box has to say whose it is.
    /// </summary>
    private void DrawDemoNames()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Heading, "Demo names");
        ImGui.TextWrapped("For screenshots. These replace names in this window only and are " +
                          "not saved. /fmf demo turns it off. Keep this panel out of the " +
                          "shot - it shows the real names.");
        ImGui.Spacing();

        var changed = false;

        foreach (var (key, real) in plugin.Demo.Names)
        {
            var alias = plugin.Demo.Aliases.GetValueOrDefault(key, string.Empty);

            ImGui.SetNextItemWidth(220);
            if (ImGui.InputTextWithHint($"{real}###demo{key}", "Stand-in name...", ref alias, 40))
                plugin.Demo.Aliases[key] = alias;

            // Applied when you finish typing: every change copies the whole index.
            if (ImGui.IsItemDeactivatedAfterEdit()) changed = true;
        }

        if (changed) plugin.RebuildDemo();
    }
}
