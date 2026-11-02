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

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##fmftabs"))
        {
            if (ImGui.BeginTabItem("Search")) { DrawSearch(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Places")) { DrawPlaces(); ImGui.EndTabItem(); }
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
                             "your bags, a retainer or the Free Company chest.");

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

        // Grouped rather than one flat list: with several estates, retainers and bags, the
        // list outgrew a screen almost immediately.
        foreach (var section in SectionNames.All)
        {
            var props = Cfg.Properties.Values.Where(p => p.Section == section)
                           .OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ToList();
            var holds = Cfg.Holdings.Values.Where(h => h.Section == section)
                           .OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            if (props.Count == 0 && holds.Count == 0) continue;

            ImGui.Separator();
            ImGui.TextColored(Heading, SectionNames.Label(section));
            ImGui.Spacing();

            foreach (var p in props) DrawProperty(p);
            foreach (var h in holds) DrawHolding(h);

            ImGui.Spacing();
        }
    }

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
    private static string Breakdown(PropertySnapshot p)
    {
        var placed = p.Pieces.Count(x => SpotNames.IsPlaced(x.Where));
        var stored = p.Pieces.Count - placed;
        var ceiling = PlacedCeiling(p);

        var placedText = ceiling == null
            ? $"{placed:N0} placed"
            : $"{placed:N0}/{ceiling:N0} placed";

        return $"{p.Pieces.Count:N0} total: {placedText}, {stored:N0} stored";
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
        ImGui.TextDisabled(p.Address);
        Provenance(p.TakenUtc, p.Partial);

        // Worth saying loudly: this list is not merely incomplete, it is missing the
        // furniture itself.
        if (!p.PlacedLoaded)
            ImGui.TextColored(Warn,
                              "The furnishings here have not loaded. Open the housing menu while " +
                              "standing at this property and it records itself properly.");

        var name = p.CustomName;
        ImGui.SetNextItemWidth(240);
        if (ImGui.InputTextWithHint($"##name{p.Key.Id}", "Call it something else...", ref name, 60))
        {
            p.CustomName = name;
            Cfg.Save(Plugin.PluginInterface);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        if (ImGui.BeginCombo($"##sec{p.Key.Id}", SectionNames.Label(p.Section)))
        {
            foreach (var sec in SectionNames.All)
                if (ImGui.Selectable(SectionNames.Label(sec), sec == p.Section))
                {
                    p.Section = sec;
                    p.SectionPinned = true;
                    Cfg.Save(Plugin.PluginInterface);
                }
            ImGui.EndCombo();
        }

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
        var label = $"{held:N0} total furnishing{(held == 1 ? "" : "s")}";

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
        ImGui.Spacing();

        if (Cfg.Properties.Count == 0 && Cfg.Holdings.Count == 0)
        {
            ImGui.TextDisabled("Nothing indexed.");
            return;
        }

        ImGui.Separator();
        ImGui.TextColored(Heading, "Properties");
        ImGui.Spacing();

        foreach (var p in Cfg.Properties.Values
                     .OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (ImGui.Button($"Forget###rm{p.Key.Id}"))
            {
                Cfg.Forget(p.Key.Id);
                Cfg.Save(Plugin.PluginInterface);
                break;
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(p.Label);
            ImGui.SameLine();
            ImGui.TextDisabled(p.Address);
        }

        if (Cfg.Holdings.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(Heading, "Bags, retainers and chests");
            ImGui.TextDisabled("These rebuild themselves the next time they are open.");
            ImGui.Spacing();

            foreach (var h in Cfg.Holdings.Values
                         .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (ImGui.Button($"Forget###rmh{h.Id}"))
                {
                    Cfg.Holdings.Remove(h.Id);
                    Cfg.Save(Plugin.PluginInterface);
                    break;
                }

                ImGui.SameLine();
                ImGui.TextUnformatted(h.DisplayName);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Forget everything"))
        {
            Cfg.ForgetEverything();
            Cfg.Save(Plugin.PluginInterface);
        }
        ImGui.TextDisabled("Clears every property, bag, retainer and chest above.");
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

    /// <summary>
    /// Alphabetical by name, then by dye - so four Wooden Handrails sit together and the
    /// Soot Black ones are not scattered through the Gobbiebag Browns.
    /// </summary>
    private static List<Hit> Sorted(IEnumerable<Hit> rows) =>
        rows.OrderBy(h => h.Piece.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Piece.StainName, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
        var full = false;
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

        foreach (var (row, count) in shown)
        {
            var p = row.Piece;
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var keep = Cfg.IsWatched(p.ItemId);
            if (ImGui.Checkbox($"##keep{p.ItemId}_{row.Location}_{p.Detail}", ref keep))
            {
                if (!Cfg.ToggleWatched(p.ItemId))
                    full = true;
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

            if (!string.IsNullOrEmpty(p.StainName))
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"({p.StainName})");
            }

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

        if (full)
            ImGui.TextColored(Warn, $"The remembered list holds {Configuration.WatchLimit} items. " +
                                    "Clear something from it first.");
    }

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
        Point("Owned Private Estates - indoor and outdoor (placed items and storeroom)");
        Point("Owned Apartment");
        Point("Owned Free Company chambers");
        Point("Inventory bags");
        Point("Retainer inventory");
        Point("Free Company chest");
        Point("Free Company estates that you have editing access to - indoor and outdoor " +
              "(placed items and storeroom)");

        ImGui.Spacing();
        ImGui.TextWrapped("Only furnishing items will be logged.");
        ImGui.TextWrapped("Shared estates - estates that you do not own - will never be recorded.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Heading, "Getting started");
        ImGui.Spacing();
        ImGui.TextWrapped("To record an inventory, open the inventory window until you see a " +
                          "prompt in the chat. When recording has succeeded you will see a " +
                          "message like:");
        ImGui.TextDisabled("    [FMF] Fishbone (retainer): 33 furnishings recorded.");
        ImGui.Spacing();
        ImGui.TextWrapped("Estates report what was found in each half, so you can check it " +
                          "against the game's own counts:");
        ImGui.TextDisabled("    [FMF] Estate Hall (Private): 300 placed, 88 stored.");
        ImGui.Spacing();
        ImGui.TextWrapped("Indoor furnishing item menus and storeroom menus must be opened " +
                          "individually. Once everything has been fully recorded, it is " +
                          "recommended to disable the chat prompt, which can be done in the " +
                          "Settings tab.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Heading, "Recording estates and inventories");
        ImGui.Spacing();
        ImGui.TextWrapped("Recording Estates: step into the estate you wish to record. Open the " +
                          "Housing menu, then open the indoor furnishing window and the storeroom " +
                          "individually to record both. This must be done for both indoors and " +
                          "outdoors. Close when recorded.");
        ImGui.Spacing();
        ImGui.TextWrapped("Recording Inventories: the FC chest, retainers, chocobo saddlebag and " +
                          "character inventory are recorded when the menu is opened. Close when " +
                          "recorded.");
        ImGui.Spacing();
        ImGui.TextWrapped("Note: your own changes to your estates will always re-record. " +
                          "Recording may drift if other editors change or withdraw furniture in " +
                          "a shared estate or shared Free Company chest.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Heading, "Lists");
        ImGui.Spacing();
        ImGui.TextWrapped("How to use the Remembered List:");
        ImGui.Spacing();
        ImGui.TextWrapped("On the Search tab, ticking the checkbox records the item's location to " +
                          "a remembered list. Useful if you're gathering multiple items in one " +
                          "trip. Can remember up to 50 items at a time.");
        ImGui.Spacing();
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextColored(Stored, "Yellow");
        ImGui.SameLine();
        ImGui.TextWrapped("is stored, white is placed. Dyes show in brackets.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Heading, "Commands");
        ImGui.Spacing();
        Point("/fmf or /fsearch - open the window");
        Point("/fmf <text> - search");
        Point("/fmf here - record this property now");
        Point("/fmf bags - rescan bags, saddlebag, retainers and the chest");
        Point("/fmf dump - write diagnostics to the log, for reporting a problem");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Heading, "Your data / removal");
        ImGui.Spacing();
        ImGui.TextWrapped("All recorded furnishing data from this plugin lives in a configuration " +
                          "file on your computer and is never sent anywhere. You can remove any " +
                          "estate or container at any time under the Removal tab.");
    }

    // ---------- settings ----------

    private void DrawSettings()
    {
        ImGui.TextWrapped("A property records itself when you open its Housing window, and again " +
                          "when the Storeroom loads. Nothing is captured as you walk around.");
        ImGui.Spacing();
        ImGui.TextWrapped("Only estates you own are ever recorded. Shared estates are excluded, " +
                          "and a property you are simply visiting is not recorded at all.");

        ImGui.Separator();

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
        if (ImGui.Checkbox("Record my bags, saddlebag and FC chest", ref bags))
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
