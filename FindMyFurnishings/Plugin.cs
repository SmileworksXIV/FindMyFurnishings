using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace FindMyFurnishings;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string Command = "/fmf";

    /// <summary>
    /// A second name for the same command. Both go to the same handler, so everything
    /// /fmf does, /fsearch does.
    /// </summary>
    private const string AliasCommand = "/fsearch";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;

    public Configuration Config { get; }

    /// <summary>
    /// Slot 0 of the object table is always the local player. The client-state property
    /// that used to give it was removed in Dalamud API 15, and an empty name here doubles
    /// as "not logged in yet".
    /// </summary>
    internal static string LocalPlayerName
    {
        get
        {
            try { return ObjectTable[0]?.Name.TextValue ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    private readonly WindowSystem windows = new("FindMyFurnishings");
    private readonly MainWindow main;
    private readonly FurnitureSheet sheet = new();
    private readonly HousingReader housing;
    private readonly InventoryScanner bags;

    private DateTime nextBagScan = DateTime.MinValue;
    private DateTime nextHousingCheck = DateTime.MinValue;

    /// <summary>The property recorded while the current housing window has been open.</summary>
    private string capturedThisSession = string.Empty;

    /// <summary>
    /// How many of that property's containers had loaded at the time. Storerooms arrive
    /// after the furnishing list does, so this rising means there is more to record.
    /// </summary>
    private int containersAtCapture = -1;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Icons.Provider = Textures;
        sheet.Load(DataManager, Log);

        housing = new HousingReader(sheet, DataManager, Log);
        bags = new InventoryScanner(sheet, Log);

        // Snapshots taken before version 2 read placed furniture from a table that was
        // not scoped to the estate, so they contain the neighbours' furnishings as well
        // as yours. There is no way to separate the two after the fact, and a list that
        // is partly someone else's is worse than no list - so they go.
        if (Config.Version < 2)
        {
            var dropped = Config.Properties.Count;
            Config.Properties.Clear();
            Config.Version = 2;
            Config.Save(PluginInterface);

            if (dropped > 0)
            {
                Log.Information($"[FMF] Cleared {dropped} property snapshot(s) recorded with the " +
                                "old, unscoped reader.");

                // Said in chat, not just the log. Properties silently disappearing looks
                // like the plugin breaking, and someone who was not told why would
                // reasonably assume their data had been lost rather than rebuilt.
                ChatGui.Print($"[FMF] Cleared {dropped} propert{(dropped == 1 ? "y" : "ies")} " +
                              "recorded by an older version that could pick up neighbouring " +
                              "estates. Visit each one again to rebuild it properly.");
            }
        }

        // Entries stored before the current naming rules get relabelled in place, so a
        // rename of the scheme does not require travelling to every property again.
        foreach (var stored in Config.Properties.Values)
            housing.Describe(stored);

        // Containers are re-filed from their id rather than waiting to be re-scanned. A
        // retainer is only readable while it is open at a bell, so one you have not
        // visited lately would otherwise keep whatever section it was stored with.
        foreach (var held in Config.Holdings.Values)
            held.Section = InventoryScanner.SectionOf(held.Id);

        Config.Save(PluginInterface);

        main = new MainWindow(this);
        windows.AddWindow(main);

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += Toggle;
        Framework.Update += OnUpdate;

        Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Find My Furnishings. \"/fmf <text>\" searches, \"/fmf here\" records " +
                          "this property, \"/fmf dump\" writes diagnostics to the log.",
        });

        Commands.AddHandler(AliasCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Same as /fmf.",
        });
    }

    public void Dispose()
    {
        Framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= Toggle;
        Commands.RemoveHandler(Command);
        Commands.RemoveHandler(AliasCommand);
        windows.RemoveAllWindows();
    }

    private void Toggle() => main.IsOpen = !main.IsOpen;

    private void OnCommand(string command, string args)
    {
        var a = args.Trim();

        switch (a.ToLowerInvariant())
        {
            case "":
                Toggle();
                return;

            case "here":
                CaptureHere(manual: true);
                return;

            case "dump":
                Dump();
                return;

            case "bags":
                ScanBags(force: true);
                return;
        }

        // Anything else is treated as a search, so "/fmf oak bed" goes straight to results.
        main.IsOpen = true;
        ChatGui.Print($"[FMF] Searching for \"{a}\".");
    }

    // ---------- automatic capture ----------

    private void OnUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;

        if (now >= nextHousingCheck)
        {
            nextHousingCheck = now.AddSeconds(2);
            WatchHousingWindow();
        }

        if (now >= nextBagScan)
        {
            nextBagScan = now.AddSeconds(10);
            if (Config.AutoIndexBags) ScanBags(force: false);
        }
    }

    /// <summary>
    /// Records a property while its housing window is open.
    ///
    /// Nothing is captured merely by walking in - the containers keep the previous
    /// estate's contents and still claim to be loaded, which filed a Free Company hall's
    /// 777 furnishings under its 150-item chamber. A housing window being open is the one
    /// condition that makes them trustworthy, so it is also the moment worth acting on.
    /// </summary>
    private void WatchHousingWindow()
    {
        if (!HousingUi.IsOpen(GameGui, out _))
        {
            // Closed again; opening it next time records afresh.
            capturedThisSession = string.Empty;
            containersAtCapture = -1;
            return;
        }

        var here = HousingReader.CurrentKeyId(housingWindowOpen: true);
        if (here.Length == 0) return;

        // Recording once per window-opening is not enough: the storeroom loads separately
        // and later, so a property captured the moment the furnishing list appeared has
        // its placed items and nothing else. Opening the storeroom raises this count, and
        // that is the signal to record again with the rest.
        var containers = HousingReader.LoadedStoreroomPages(HousingReader.IsIndoors());

        if (here == capturedThisSession && containers <= containersAtCapture) return;

        capturedThisSession = here;
        containersAtCapture = containers;
        CaptureHere(manual: false);
    }

    /// <summary>
    /// What this kind of property can actually hold, where that is less than its
    /// containers can.
    ///
    /// A tripwire, not an inventory limit. The housing containers are shared: a Free
    /// Company hall fills twelve fifty-slot pages, and standing in its 150-item chamber
    /// reads those same pages and gets the hall's furniture. A count that cannot be true
    /// for this property means the data is not this property's.
    ///
    /// Null means no useful cap - the containers themselves are the bound, so a hall at
    /// 591/600 is simply a full hall and must not be refused.
    /// </summary>
    private static (int Placed, int Stored) Limits(PropertySnapshot snap) => snap.Estate switch
    {
        EstateType.PersonalChambers => (150, 150),
        EstateType.ApartmentRoom or EstateType.ApartmentBuilding => (150, 150),

        // Estates come in three sizes - cottage 300/40, house 450/60, mansion 600/80 -
        // and nothing verified here tells them apart, so the cap is the largest of the
        // three. A cottage reading 500 is therefore allowed through, which is fine: this
        // exists to catch one property's contents turning up under another's name, not
        // to police the game's own limits.
        _ => snap.Outdoors ? (80, 80) : (600, 600),
    };

    /// <summary>
    /// Stores a snapshot, carrying forward any name you gave the property.
    ///
    /// Re-capture replaces the whole record, so without this a rename would be quietly
    /// undone by the next visit.
    /// </summary>
    private void Save(PropertySnapshot snap)
    {
        if (Config.Properties.TryGetValue(snap.Key.Id, out var previous))
            snap.CustomName = previous.CustomName;

        Config.Properties[snap.Key.Id] = snap;
        Config.Save(PluginInterface);
    }

    /// <summary>
    /// Records the property you are standing in. Only ever called because you asked.
    /// </summary>
    public void CaptureHere(bool manual)
    {
        // The containers are only trustworthy for this property once the game has opened a
        // housing window for it - reading them in a Free Company chamber otherwise
        // returned the hall's furnishings. It doubles as the access test, since the game
        // opens one only where you may edit.
        var windowOpen = HousingUi.IsOpen(GameGui, out _);

        if (!HousingReader.CanIndexHere(windowOpen, out var why))
        {
            if (manual) ChatGui.PrintError($"[FMF] Not recording here: {why}.");
            return;
        }

        // A shared estate is somebody else's. Walking into one also clears anything
        // recorded about it before that rule existed.
        if (HousingReader.IsSharedEstate())
        {
            var id = HousingReader.CurrentKeyId(windowOpen);
            if (id.Length > 0 && Config.Properties.Remove(id)) Config.Save(PluginInterface);

            if (manual)
                ChatGui.PrintError("[FMF] This is a shared estate. Only estates you own are " +
                                   "recorded, not ones you are a guest on.");
            return;
        }

        if (!windowOpen)
        {
            if (manual)
                ChatGui.PrintError("[FMF] Open the Housing menu (and the Storeroom) first, " +
                                   "then run this again.");
            return;
        }

        var snap = housing.Capture(windowOpen);
        if (snap == null)
        {
            if (manual)
                ChatGui.PrintError("[FMF] Could not read this property. Run /fmf dump and send " +
                                   "the [FMF] lines.");
            return;
        }

        // A read that caught none of the placed containers says nothing about this house,
        // and must never replace one that did.
        if (Config.Properties.TryGetValue(snap.Key.Id, out var had)
            && !snap.PlacedLoaded && had.PlacedLoaded)
        {
            ChatGui.PrintError("[FMF] The furnishings here have not loaded. Open the Housing " +
                               "menu and try again - the existing record has been kept.");
            return;
        }

        var cap = Limits(snap);
        var placed = snap.Pieces.Count(x => SpotNames.IsPlaced(x.Where));
        var stored = snap.Pieces.Count - placed;

        if (placed > cap.Placed || stored > cap.Stored)
        {
            Log.Warning($"[FMF] Refused {placed} placed / {stored} stored at {snap.Address}; " +
                        $"{snap.Estate?.ToString() ?? "this property"} holds at most " +
                        $"{cap.Placed}/{cap.Stored}.");

            ChatGui.PrintError($"[FMF] The game returned {placed:N0} placed and {stored:N0} " +
                               $"stored, but this property holds at most {cap.Placed}/" +
                               $"{cap.Stored}. That is another estate's contents, so nothing " +
                               "was recorded.");
            return;
        }

        Save(snap);

        // A typed command always answers; the automatic one can be silenced.
        if (manual || Config.AnnounceCaptures)
            ChatGui.Print($"[FMF] {snap.Label}: {Describe(snap)}");
    }

    private void ScanBags(bool force)
    {
        var who = LocalPlayerName;
        if (string.IsNullOrEmpty(who)) return;

        Store(bags.Bag(who));
        Store(bags.Saddlebag(who));
        Store(bags.Retainer());
        Store(bags.FreeCompany());

        if (force)
            ChatGui.Print($"[FMF] Scanned bags, saddlebag and FC chest - " +
                          $"{Config.Holdings.Values.Sum(h => h.Pieces.Count):N0} furnishings held.");
    }

    /// <summary>
    /// What was recorded, said in full.
    ///
    /// A bare total cannot be checked against anything. Naming both counts lets you hold
    /// the message up against the game's own placed and stored figures, which is how the
    /// Free Company chamber reading the hall's furniture was caught.
    /// </summary>
    private static string Describe(PropertySnapshot snap)
    {
        var placed = snap.Pieces.Count(x => SpotNames.IsPlaced(x.Where));
        var stored = snap.Pieces.Count - placed;

        var parts = new List<string>();

        parts.Add(snap.PlacedLoaded
            ? $"{placed:N0} placed"
            : "placed furnishings NOT loaded");

        parts.Add(snap.StoreroomLoaded
            ? $"{stored:N0} stored"
            : "storeroom NOT loaded");

        var line = string.Join(", ", parts) + ".";

        if (!snap.PlacedLoaded) line += " Open the Housing window here.";
        else if (!snap.StoreroomLoaded) line += " Open the Storeroom to include what is stored.";

        return line;
    }

    /// <summary>
    /// Stores a container snapshot, and says so when its contents actually changed.
    ///
    /// Containers are rescanned every few seconds, so announcing every scan would be
    /// constant noise. Announcing only a change means opening a retainer confirms itself
    /// and then goes quiet, which is the only moment the confirmation is worth anything.
    /// </summary>
    private void Store(HoldingSnapshot? snap)
    {
        if (snap == null) return;

        var changed = !Config.Holdings.TryGetValue(snap.Id, out var had)
                      || had.Pieces.Count != snap.Pieces.Count;

        Config.Holdings[snap.Id] = snap;
        Config.Save(PluginInterface);

        if (!changed || !Config.AnnounceCaptures) return;

        var n = snap.Pieces.Count;
        ChatGui.Print($"[FMF] {snap.DisplayName}: {n:N0} furnishing{(n == 1 ? "" : "s")} recorded.");
    }

    // ---------- diagnostics ----------

    /// <summary>
    /// Writes what the plugin can see to the log, so a mapping that comes out wrong can be
    /// checked against the room you are standing in rather than guessed at.
    /// </summary>
    private void Dump()
    {
        var mgr = HousingManager.Instance();
        if (mgr == null)
        {
            ChatGui.PrintError("[FMF] No housing manager - are you in a housing zone?");
            return;
        }

        Log.Information("[FMF] ---- dump ----");
        Log.Information($"[FMF] inside={mgr->IsInside()} outside={mgr->IsOutside()} " +
                        $"permissions={mgr->HasHousePermissions()}");
        Log.Information($"[FMF] indexable here: " +
                        $"{HousingReader.CanIndexHere(HousingUi.IsOpen(GameGui, out _), out var verdict)} " +
                        $"({verdict})");

        var housingOpen = HousingUi.IsOpen(GameGui, out var openAddon);
        Log.Information($"[FMF] housing window open: {housingOpen}" +
                        (housingOpen ? $" ({openAddon})" : string.Empty));

        foreach (var name in HousingUi.Candidates)
        {
            var ptr = GameGui.GetAddonByName(name);
            if (ptr != nint.Zero)
                Log.Information($"[FMF]   addon present: {name}");
        }
        Log.Information($"[FMF] ward={mgr->GetCurrentWard()} plot={mgr->GetCurrentPlot()} " +
                        $"room={mgr->GetCurrentRoom()} division={mgr->GetCurrentDivision()}");

        var fm = mgr->GetFurnitureManager();
        if (fm == null)
        {
            Log.Information("[FMF] furniture manager: null");
        }
        else
        {
            var indoors = mgr->IsInside();
            var slots = fm->FurnitureMemory;
            var shown = 0;

            for (var i = 0; i < slots.Length - 1 && shown < 25; i++)
            {
                var f = slots[i];
                if (f.Id == 0) continue;

                var itemId = sheet.ItemIdForPlaced(f.Id, indoors);
                if (itemId == 0)
                    Log.Information($"[FMF]   slot {i}: rawId={f.Id} UNRESOLVED " +
                                    sheet.ExplainPlaced(f.Id, indoors));
                else
                    Log.Information($"[FMF]   slot {i}: rawId={f.Id} -> item={itemId} " +
                                    $"\"{sheet.NameOf(itemId)}\" stain={f.Stain}");
                shown++;
            }

            Log.Information($"[FMF] furniture slots={slots.Length}, first {shown} non-empty shown.");
        }

        var inv = InventoryManager.Instance();
        if (inv != null)
        {
            foreach (var t in new[]
                     {
                         InventoryType.HousingInteriorStoreroom1,
                         InventoryType.HousingInteriorPlacedItems1,
                         InventoryType.HousingExteriorStoreroom,
                         InventoryType.HousingExteriorPlacedItems,
                     })
            {
                var c = inv->GetInventoryContainer(t);
                Log.Information(c == null
                    ? $"[FMF] {t}: container null"
                    : $"[FMF] {t}: loaded={c->IsLoaded} size={c->Size}");
            }
        }

        // One shared code path with detection, so the diagnosis describes the read the
        // plugin actually performs rather than a parallel one that might behave differently.
        var houseId = HousingManager.Instance() == null
            ? 0UL
            : HousingManager.Instance()->GetCurrentHouseId().Id;

        var estate = HousingReader.EstateOf(houseId, out var how);
        Log.Information($"[FMF] current houseId={houseId}");
        Log.Information($"[FMF] estate type: {estate?.ToString() ?? "unresolved"} ({how})");

        try
        {
            var telepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance();
            var list = telepo == null ? null : telepo->UpdateAetheryteList();

            if (list == null || list->First == null || list->Last == null || list->Last < list->First)
            {
                Log.Information("[FMF] teleport list unavailable.");
            }
            else
            {
                var count = (int)(list->Last - list->First);
                var shown = 0;

                // Only entries the game has filled in. An unpopulated list is pages of
                // 0xFF, which is noise, not evidence.
                for (var i = 0; i < count && shown < 30; i++)
                {
                    var e = list->First + i;
                    if (e->HouseId.Id == ulong.MaxValue || (byte)e->EstateType == 0xFF) continue;

                    Log.Information($"[FMF]   telepo: estate={e->EstateType} ward={e->Ward} " +
                                    $"plot={e->Plot} sub={e->SubIndex} houseId={e->HouseId.Id} " +
                                    $"{(e->HouseId.Id == houseId ? "<== THIS ONE" : string.Empty)}");
                    shown++;
                }

                Log.Information($"[FMF] teleport entries={count}, filled and shown={shown}.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[FMF] teleport list unreadable: {ex.Message}");
        }

        ChatGui.Print("[FMF] Dump written to the log - open /xllog and copy the [FMF] lines.");
    }
}
