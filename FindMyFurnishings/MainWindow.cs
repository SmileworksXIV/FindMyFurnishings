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

    private Configuration Cfg => plugin.Config;

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
                    line + $" {hidden:N0} more {(hidden == 1 ? "is" : "are")} placed and hidden " +
                           "by \"View stored items only\".");
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

        ImGui.Spacing();
        DrawRemembered();

        if (!searching) return;

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
    private string cachedQuery = "\u0000";
    private bool cachedFilter;
    private int cachedRevision = -1;

    /// <summary>
    /// Rebuilds the search results, but only when the question or the answer could have
    /// changed: a different query, a different filter, or an index that has been written
    /// to since. Otherwise the previous results stand and the frame costs nothing.
    /// </summary>
    private void Refresh(string q)
    {
        if (cachedQuery == q && cachedFilter == Cfg.SearchStoredOnly
            && cachedRevision == Configuration.Revision)
            return;

        cachedQuery = q;
        cachedFilter = Cfg.SearchStoredOnly;
        cachedRevision = Configuration.Revision;

        cachedHits = q.Length == 0 ? new List<Hit>() : Find(q, out cachedHidden);
        if (q.Length == 0) cachedHidden = 0;
    }

    /// <param name="hiddenByFilter">
    /// How many matches "View stored items only" held back. Counted on the way past
    /// rather than by searching a second time - a search that finds three things and
    /// shows none of them looks exactly like a search that found nothing, so the number
    /// is needed, but it is not worth a second walk of the whole index for.
    /// </param>
    private List<Hit> Find(string q, out int hiddenByFilter)
    {
        var needle = q.Trim();
        var hits = new List<Hit>();
        var hiding = Cfg.SearchStoredOnly;
        var hidden = 0;

        void Consider(string label, DateTime taken, bool partial, Piece piece)
        {
            if (!piece.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)) return;

            if (hiding && SpotNames.IsPlaced(piece.Where))
            {
                hidden++;
                return;
            }

            hits.Add(new Hit(label, taken, partial, piece));
        }

        foreach (var p in Cfg.Properties.Values)
            foreach (var piece in p.Pieces)
                Consider(p.Label, p.TakenUtc, p.Partial, piece);

        foreach (var h in Cfg.Holdings.Values)
            foreach (var piece in h.Pieces)
                Consider(h.DisplayName, h.TakenUtc, h.Partial, piece);

        hiddenByFilter = hidden;

        return hits
            .OrderByDescending(h => h.Piece.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            .ThenBy(h => h.Piece.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Location, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private readonly Dictionary<uint, List<Hit>> remembered = new();
    private int rememberedRevision = -1;
    private static readonly List<Hit> NoHits = new();

    /// <summary>
    /// Every location of every remembered item, in one walk of the index rather than one
    /// walk per item per frame. Fifty remembered furnishings used to mean fifty full
    /// scans, sixty times a second.
    /// </summary>
    private Dictionary<uint, List<Hit>> Remembered()
    {
        if (rememberedRevision == Configuration.Revision) return remembered;

        rememberedRevision = Configuration.Revision;
        remembered.Clear();

        if (Cfg.Watched.Count == 0) return remembered;

        foreach (var id in Cfg.Watched) remembered[id] = new List<Hit>();

        foreach (var p in Cfg.Properties.Values)
            foreach (var piece in p.Pieces)
                if (remembered.TryGetValue(piece.ItemId, out var into))
                    into.Add(new Hit(p.Label, p.TakenUtc, p.Partial, piece));

        foreach (var h in Cfg.Holdings.Values)
            foreach (var piece in h.Pieces)
                if (remembered.TryGetValue(piece.ItemId, out var into))
                    into.Add(new Hit(h.DisplayName, h.TakenUtc, h.Partial, piece));

        return remembered;
    }

    /// <summary>
    /// The shopping list. Tick items anywhere in the plugin and they collect here with
    /// every place they sit, so a run to fetch six things is one list rather than six
    /// searches and a memory test.
    /// </summary>
    private void DrawRemembered()
    {
        if (Cfg.Watched.Count == 0) return;

        var header = $"Remembered ({Cfg.Watched.Count})###remembered";
        if (ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            ImGui.TextDisabled("Ticking the checkbox records the item's location to a remembered " +
                               "list. Useful if you're gathering multiple items in one trip.");
            ImGui.Spacing();

            foreach (var itemId in Cfg.Watched.ToList())
            {
                var all = Remembered().TryGetValue(itemId, out var found) ? found : NoHits;

                // The same filter the search uses, so the toggle means one thing
                // everywhere on the tab.
                var where = Cfg.SearchStoredOnly
                    ? all.Where(h => !SpotNames.IsPlaced(h.Piece.Where)).ToList()
                    : all;

                var name = all.Count > 0 ? all[0].Piece.Name : $"Item {itemId}";

                if (ImGui.SmallButton($"Forget###unwatch{itemId}"))
                {
                    Cfg.ToggleWatched(itemId);
                    Cfg.Save(Plugin.PluginInterface);
                    break;
                }

                ImGui.SameLine();
                Icons.Draw(all.Count > 0 ? all[0].Piece.IconId : 0, IconSize);
                ImGui.SameLine();
                ImGui.TextUnformatted(name);

                ImGui.Indent();
                if (all.Count == 0)
                {
                    // Ticked from a snapshot that has since been forgotten or rebuilt.
                    ImGui.TextDisabled("Not in any current snapshot.");
                }
                else if (where.Count == 0)
                {
                    // Every copy is placed. Saying nothing here would read as "you do not
                    // own one", which is the opposite of true.
                    ImGui.TextDisabled($"{all.Count} placed, none stored - hidden by " +
                                       "\"View stored items only\".");
                }
                else
                {
                    // Six of the same lamp in the same room is one fact, not six lines.
                    // Grouped on the text each row would have printed, so anything that
                    // actually differs - a storeroom rather than the floor, another
                    // property, a different dye - still gets a row of its own.
                    // Same setting as the tables, so "stacked" means one thing everywhere.
                    var lines = new List<(string Text, bool Stored, int Count)>();

                    foreach (var hit in where)
                    {
                        var stored = hit.Piece.Where is Spot.IndoorStoreroom or Spot.OutdoorStoreroom;
                        var text = $"{hit.Location} - {SpotNames.Label(hit.Piece.Where)}" +
                                   (string.IsNullOrEmpty(hit.Piece.Detail) ? string.Empty
                                                                           : $" ({hit.Piece.Detail})");

                        var at = Cfg.StackIdentical
                            ? lines.FindIndex(l => l.Text == text && l.Stored == stored)
                            : -1;

                        if (at >= 0) lines[at] = (text, stored, lines[at].Count + 1);
                        else lines.Add((text, stored, 1));
                    }

                    foreach (var (text, stored, count) in lines)
                    {
                        // Plain "x" rather than the multiplication sign: the font atlas is
                        // not guaranteed to carry it, and a missing glyph box would look
                        // like a bug in the very list meant to be easier to read.
                        var label = count > 1 ? $"{text}  x{count}" : text;

                        if (stored) ImGui.TextColored(Stored, label);
                        else ImGui.TextUnformatted(label);
                    }
                }
                ImGui.Unindent();
                ImGui.Spacing();
            }

            if (ImGui.Button("Clear the list"))
            {
                Cfg.Watched.Clear();
                Cfg.Save(Plugin.PluginInterface);
            }

            ImGui.Unindent();
        }

        ImGui.Separator();
    }

    // ---------- places ----------

    private static readonly Vector4 Heading = new(0.85f, 0.78f, 0.55f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.75f, 0.25f, 1f);

    /// <summary>#FDFD66 - marks an item that is stored rather than placed.</summary>
    private static readonly Vector4 Stored = new(0.992f, 0.992f, 0.4f, 1f);

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
        var me = Plugin.LocalPlayerName;

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
                : fcProps.Where(p => p.FreeCompany == company).ToList();

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
        string.IsNullOrWhiteSpace(who) || who == "Unknown" ? "Free Company" : $"Free Company ({who})";

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
    /// How many placed pieces this kind of property can hold, where that is knowable.
    ///
    /// Apartments and chambers are a flat 150. Estates come in three sizes - cottage,
    /// house, mansion - and nothing the plugin can read tells them apart, so no ceiling is
    /// claimed for those rather than quoting a mansion's at a cottage.
    /// </summary>
    private static int? PlacedCeiling(PropertySnapshot p) => p.Estate switch
    {
        EstateType.PersonalChambers => 150,
        EstateType.ApartmentRoom or EstateType.ApartmentBuilding => 150,
        _ => null,
    };

    /// <summary>
    /// A property header that explains its own number: what is there in total, how much of
    /// it is out in the room, and how much is in the storeroom. "(180)" on its own left
    /// you working out which of the three it meant.
    /// </summary>
    /// <summary>
    /// How much storeroom this property has, where that is knowable.
    ///
    /// The same 150 as the floor for a chamber, but kept as its own number rather than
    /// reusing the placed ceiling: the two are only equal by coincidence, and a published
    /// table claiming chambers placed 100 was briefly believed until a chamber holding 146
    /// disproved it. Estates still get none - their storeroom matches their size, and
    /// nothing the plugin can read tells a cottage from a mansion, so a denominator there
    /// would be a guess dressed as a fact.
    /// </summary>
    private static int? StoredCeiling(PropertySnapshot p) => p.Estate switch
    {
        EstateType.PersonalChambers => 150,
        EstateType.ApartmentRoom or EstateType.ApartmentBuilding => 150,
        _ => null,
    };

    private static string Breakdown(PropertySnapshot p)
    {
        var placed = p.Pieces.Count(x => SpotNames.IsPlaced(x.Where));
        var stored = p.Pieces.Count - placed;

        var placedCeiling = PlacedCeiling(p);
        var storedCeiling = StoredCeiling(p);

        var placedText = placedCeiling == null
            ? $"{placed:N0} placed"
            : $"{placed:N0}/{placedCeiling:N0} placed";

        var storedText = storedCeiling == null
            ? $"{stored:N0} stored"
            : $"{stored:N0}/{storedCeiling:N0} stored";

        return $"{placedText}, {storedText}";
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
            new Hit(p.Label, p.TakenUtc, p.Partial, x))), showLocation: false);

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
            new Hit(h.DisplayName, h.TakenUtc, h.Partial, x))), showLocation: false);
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

        var index = 0;

        foreach (var (row, count) in shown)
        {
            var p = row.Piece;
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var keep = Cfg.IsWatched(p.ItemId);

            // Numbered by row. Built from the item and its location, two rows could share
            // an id - the same chair placed and stored in one property - and the two boxes
            // then behaved as one.
            if (ImGui.Checkbox($"##keep{index++}", ref keep))
            {
                if (!Cfg.ToggleWatched(p.ItemId))
                {
                    listFullUntil = DateTime.UtcNow.AddSeconds(5);
                    listFullTable = id;
                }
                else
                    Cfg.Save(Plugin.PluginInterface);
            }

            ImGui.TableNextColumn();
            Icons.Draw(p.IconId, IconSize);

            // Stored items are tinted so a list can be read at a glance for what is
            // actually out in the room versus what is sitting in the storeroom.
            var stored = p.Where is Spot.IndoorStoreroom or Spot.OutdoorStoreroom;

            // Plain "x" rather than the multiplication sign: the font atlas is not
            // guaranteed to carry it, and a missing glyph would look like a fault.
            var label = count > 1 ? $"{p.Name}  x{count:N0}" : p.Name;

            ImGui.TableNextColumn();
            if (stored) ImGui.TextColored(Stored, label);
            else ImGui.TextUnformatted(label);

            ImGui.TableNextColumn();
            var where = string.IsNullOrEmpty(p.Detail)
                ? SpotNames.Label(p.Where)
                : $"{SpotNames.Label(p.Where)} - {p.Detail}";

            if (stored) ImGui.TextColored(Stored, where);
            else ImGui.TextUnformatted(where);

            if (showLocation)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Location);
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

        // Held on screen for a few seconds. Shown only on the frame of the click, it was
        // gone before it could be read.
        if (listFullTable == id && DateTime.UtcNow < listFullUntil)
            ImGui.TextColored(Warn, $"The remembered list holds {Configuration.WatchLimit} items. " +
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
                          "estates and inventories that you own.");

        ImGui.Spacing();
        ImGui.TextUnformatted("The following is recordable:");
        ImGui.Spacing();
        Point("Private Estates - indoors and yard (placed items and storeroom)");
        Point("Apartment");
        Point("Private Chambers");
        Point("Free Company estates you have editing access to - indoors and yard");
        Point("Inventory, chocobo saddlebag and retainers");

        ImGui.Spacing();
        ImGui.TextWrapped("Only furnishing items are logged. Estates you don't own are only " +
                          "recorded if you turn that on in Settings.");

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
        ImGui.TextWrapped("Tick the checkbox next to an item and it stays on the Search tab with " +
                          "every place it sits. Useful if you're gathering multiple items in one " +
                          "trip. Holds up to 50.");
        ImGui.Spacing();
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextColored(Stored, "Yellow");
        ImGui.SameLine();
        ImGui.TextWrapped("is stored, white is placed.");

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
        ImGui.TextWrapped("A property records itself when you open its Housing window, and again " +
                          "when the Storeroom loads. Nothing is captured as you walk around.");
        ImGui.Spacing();
        ImGui.TextWrapped("Estates you own are recorded. Shared estates are left out unless you " +
                          "turn them on below, and a property you are simply visiting is never " +
                          "recorded.");

        ImGui.Separator();

        var shared = Cfg.RecordSharedEstates;
        if (ImGui.Checkbox("Record estates you have editing rights on", ref shared))
        {
            Cfg.RecordSharedEstates = shared;
            Cfg.Save(Plugin.PluginInterface);
        }
        ImGui.TextWrapped("Off by default, so visiting a friend's house does not file it under " +
                          "your own index. This is your setting about your own index - it does " +
                          "not ask the owner, and nothing here could: a plugin only ever sees " +
                          "its own game client, and the owner may not run this plugin at all.");
        ImGui.Spacing();

        var say = Cfg.AnnounceCaptures;
        if (ImGui.Checkbox("Confirm each recording in chat", ref say))
        {
            Cfg.AnnounceCaptures = say;
            Cfg.Save(Plugin.PluginInterface);
        }
        ImGui.TextWrapped("Off silences the routine confirmations, which appear every time you " +
                          "open a Housing window. /fmf here still confirms, and anything that " +
                          "went wrong is still reported.");

        var stack = Cfg.StackIdentical;
        if (ImGui.Checkbox("Show identical items as one row with a count", ref stack))
        {
            Cfg.StackIdentical = stack;
            Cfg.Save(Plugin.PluginInterface);
        }
        ImGui.TextWrapped("Six of the same lamp in the same room become \"x6\" on one line, in " +
                          "every list. Off lists each one separately.");

        var bags = Cfg.AutoIndexBags;
        if (ImGui.Checkbox("Record my bags, saddlebag and retainers", ref bags))
        {
            Cfg.AutoIndexBags = bags;
            Cfg.Save(Plugin.PluginInterface);
        }
        ImGui.TextWrapped("Furnishings only. Everything else in those containers is ignored.");

        var stale = Cfg.StaleAfterDays;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Flag snapshots older than (days)", ref stale))
        {
            Cfg.StaleAfterDays = Math.Max(0, stale);
            Cfg.Save(Plugin.PluginInterface);
        }
        ImGui.TextWrapped("Your own changes always re-record, because rearranging means opening the " +
                          "Housing window. This is for a Free Company house someone else may have " +
                          "changed. 0 turns it off.");

        ImGui.Separator();
        ImGui.TextDisabled("Everything this plugin knows lives in its own config file and is " +
                           "never sent anywhere. Deleting is on the Removal tab.");
    }
}
