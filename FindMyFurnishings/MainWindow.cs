using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

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
        var hits = searching ? Find(search) : null;

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
            ImGui.TextDisabled($"{hits!.Count:N0} {(hits.Count == 1 ? "match" : "matches")}.");
        }

        ImGui.Spacing();

        var storedOnly = Cfg.SearchStoredOnly;
        if (ImGui.Checkbox("Stored items only", ref storedOnly))
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
            ImGui.TextDisabled(Cfg.SearchStoredOnly
                ? "Nothing stored matching that. Untick \"Stored items only\" to include placed furnishings."
                : "Nothing matching, in anywhere visited so far.");
            return;
        }

        DrawTable("##hits", hits, showLocation: true);
    }

    /// <summary>
    /// Case-insensitive substring match over every snapshot. Exact prefix matches sort
    /// first, so searching "bed" puts beds above "Riviera Bedside Table".
    /// </summary>
    private List<Hit> Find(string q)
    {
        var needle = q.Trim();
        var hits = new List<Hit>();

        bool Wanted(Piece piece) =>
            piece.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            && (!Cfg.SearchStoredOnly || !SpotNames.IsPlaced(piece.Where));

        foreach (var p in Cfg.Properties.Values)
            foreach (var piece in p.Pieces)
                if (Wanted(piece))
                    hits.Add(new Hit(p.Label, p.TakenUtc, p.Partial, piece));

        foreach (var h in Cfg.Holdings.Values)
            foreach (var piece in h.Pieces)
                if (Wanted(piece))
                    hits.Add(new Hit(h.DisplayName, h.TakenUtc, h.Partial, piece));

        return hits
            .OrderByDescending(h => h.Piece.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            .ThenBy(h => h.Piece.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Location, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Every recorded location of one item, wherever it is.</summary>
    private List<Hit> Everywhere(uint itemId)
    {
        var hits = new List<Hit>();

        foreach (var p in Cfg.Properties.Values)
            foreach (var piece in p.Pieces)
                if (piece.ItemId == itemId)
                    hits.Add(new Hit(p.Label, p.TakenUtc, p.Partial, piece));

        foreach (var h in Cfg.Holdings.Values)
            foreach (var piece in h.Pieces)
                if (piece.ItemId == itemId)
                    hits.Add(new Hit(h.DisplayName, h.TakenUtc, h.Partial, piece));

        return hits;
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
                var all = Everywhere(itemId);

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
                                       "\"Stored items only\".");
                }
                else
                {
                    foreach (var hit in where)
                    {
                        var stored = hit.Piece.Where is Spot.IndoorStoreroom or Spot.OutdoorStoreroom;
                        var line = $"{hit.Location} - {SpotNames.Label(hit.Piece.Where)}" +
                                   (string.IsNullOrEmpty(hit.Piece.Detail) ? string.Empty
                                                                           : $" ({hit.Piece.Detail})");

                        if (stored) ImGui.TextColored(Stored, line);
                        else ImGui.TextUnformatted(line);
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

    private void DrawProperty(PropertySnapshot p)
    {
        if (!ImGui.CollapsingHeader($"{p.Label} ({p.Pieces.Count:N0})###prop{p.Key.Id}")) return;

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

        DrawTable($"##t{p.Key.Id}", Sorted(p.Pieces.Select(x =>
            new Hit(p.Label, p.TakenUtc, p.Partial, x))), showLocation: false);

        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawHolding(HoldingSnapshot h)
    {
        if (!ImGui.CollapsingHeader($"{h.DisplayName} ({h.Pieces.Count:N0})###hold{h.Id}")) return;

        ImGui.Indent();
        Provenance(h.TakenUtc, h.Partial);
        DrawTable($"##th{h.Id}", Sorted(h.Pieces.Select(x =>
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

    private void DrawTable(string id, List<Hit> rows, bool showLocation)
    {
        var cols = showLocation ? 5 : 4;
        var full = false;

        if (!ImGui.BeginTable(id, cols, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                                        ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("##keep", ImGuiTableColumnFlags.WidthFixed, 26);
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, IconSize + 6);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthStretch, 2);
        if (showLocation) ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableHeadersRow();

        foreach (var row in rows)
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

            ImGui.TableNextColumn();
            if (stored) ImGui.TextColored(Stored, p.Name);
            else ImGui.TextUnformatted(p.Name);

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
