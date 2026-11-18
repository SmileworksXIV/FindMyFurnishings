using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
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
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
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

    /// <summary>
    /// Keep re-reading the bags this often, until this moment passes.
    ///
    /// Storing a piece moves it from your inventory into the property, so half the change
    /// lands in containers the housing watcher never looks at. On the ordinary ten-second
    /// sweep that left the same piece listed in both places at once for several seconds.
    /// A move sets this, and the two halves catch up together.
    /// </summary>
    private DateTime nextHousingCheck = DateTime.MinValue;

    /// <summary>
    /// The last cheap reading of the watched containers, so the expensive one is only
    /// built when something has actually moved.
    /// </summary>
    private long lastBagSignature;

    /// <summary>The property recorded while the current housing window has been open.</summary>
    private string capturedThisSession = string.Empty;

    /// <summary>
    /// The containers as they looked at the last check. Compared on every tick so a full
    /// capture only happens when something actually moved.
    /// </summary>
    private long lastRawSignature;

    /// <summary>
    /// A reading seen once but not yet acted on.
    ///
    /// The containers are not swapped atomically when a Housing window opens. For a tick
    /// or two they hold a mixture of the property you have left and the one you are in -
    /// which is how a Free Company hall reported 590 placed / 150 stored and then settled
    /// at 150 / 238 a moment later. Both readings claimed to be loaded, and 590 is a legal
    /// count for a hall, so nothing downstream could tell them apart. Waiting for the same
    /// number twice running can.
    /// </summary>
    private long pendingRaw = NoReading;

    /// <summary>When the current pending reading first appeared.</summary>
    private DateTime pendingSince = DateTime.MinValue;

    private const long NoReading = long.MinValue;

    /// <summary>
    /// How long the containers must hold completely still before the FIRST reading of a
    /// visit is believed.
    ///
    /// One tick was not enough: a hall caught mid-swap sat at 590 placed for several ticks
    /// before settling at 150, long enough to be written down and announced. Nothing in
    /// the data distinguishes the wrong number from the right one, so the only usable
    /// signal left is that the wrong one does not last.
    ///
    /// Paid once, on arrival. A piece moved while the window is already open still records
    /// on the next tick, because by then the containers have long since settled.
    /// </summary>
    private const double FirstReadingSettle = 1.5;

    /// <summary>When the current Housing window opened, or MinValue when none is.</summary>
    private DateTime housingOpenedAt = DateTime.MinValue;

    private bool hintShown;

    /// <summary>
    /// The last line said about each property or container, so the same sentence is never
    /// printed twice running.
    ///
    /// A backstop, not a feature. Every repeat so far has been a symptom of something else
    /// - containers flapping between two states, a record that could not be written and so
    /// kept looking incomplete - and each was fixed at its source. But "say it once" is a
    /// rule that can be stated absolutely, and a wrong count said once is a far smaller
    /// problem than a right count said four times.
    /// </summary>
    private readonly Dictionary<string, string> lastSpoken = new();

    /// <summary>
    /// How long a Housing window may sit open having recorded nothing before the plugin
    /// says what to do about it.
    /// </summary>
    private const double HintSeconds = 2.0;

    /// <summary>
    /// How long to keep watching after the Housing window shuts.
    ///
    /// Moving a piece is a round trip to the server: the containers do not change the
    /// instant you click, and closing the window straight afterwards used to beat the
    /// confirmation home. The containers stay readable for a moment after the addon goes,
    /// so the watch continues through it and picks up the change when it lands.
    /// </summary>
    private DateTime watchUntil = DateTime.MinValue;

    private const double GraceSeconds = 6;

    /// <summary>
    /// When the current visit's first readable tick happened.
    ///
    /// Opening a Housing window does not fill its containers in the same frame. At a
    /// 150ms tick we now get there first, which is why the chat used to open with
    /// "placed furnishings NOT loaded, storeroom NOT loaded" - a line that could only
    /// ever appear while the window was open, telling you to open it. Nothing is said
    /// until the read is whole, or until the game has had this long to make it whole.
    /// </summary>
    private DateTime visitStartedAt = DateTime.MinValue;

    private const double SettleSeconds = 2.5;

    /// <summary>
    /// Look again even though the containers have not moved.
    ///
    /// Set when a reading was put off rather than refused - a storeroom that reads empty
    /// is only believed once it has stayed empty - because nothing else would bring the
    /// watcher back: an unchanged property that has already been recorded is otherwise
    /// left alone, which is how an emptied storeroom came to be never recorded at all.
    /// </summary>
    private bool recheck;

    /// <summary>
    /// The earliest a refused or deferred reading is tried again.
    ///
    /// A capture that returned false used to be repeated on every tick - seven full reads
    /// a second for as long as the window stayed open, and, where the refusal spoke, the
    /// same line in chat seven times a second. Once a second is as fast as any of the
    /// reasons for waiting can change.
    /// </summary>
    private DateTime retryNotBefore = DateTime.MinValue;

    private const double RetrySeconds = 1.0;

    /// <summary>The property whose storeroom is currently reading empty, and since when.</summary>
    private string emptyStoreKey = string.Empty;
    private DateTime emptyStoreSince = DateTime.MinValue;

    /// <summary>
    /// How long a storeroom must go on reading empty before that is believed.
    ///
    /// A storeroom reports itself loaded a moment before it is filled, so one empty read
    /// proves nothing. But that moment is short, and an empty reading that is still there
    /// two seconds later is a storeroom somebody emptied.
    /// </summary>
    private const double EmptyStoreroomSettle = 2.0;


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

        // A chamber is a numbered room that is not an apartment. Anything else recorded as
        // one was filed by the old detection, which called every unmatched property in the
        // same ward as one of your estates a chamber. The stored type outranks everything
        // when a record is relabelled, so it is cleared here and the address decides
        // instead; the next visit settles it properly.
        foreach (var stored in Config.Properties.Values)
            if (stored.Estate == EstateType.PersonalChambers
                && (stored.Key.Room <= 0 || stored.Key.IsApartment))
                stored.Estate = null;

        // Entries stored before the current naming rules get relabelled in place, so a
        // rename of the scheme does not require travelling to every property again.
        foreach (var stored in Config.Properties.Values)
            housing.Describe(stored);

        // Containers are re-filed from their id rather than waiting to be re-scanned. A
        // retainer is only readable while it is open at a bell, so one you have not
        // visited lately would otherwise keep whatever section it was stored with.
        foreach (var held in Config.Holdings.Values)
        {
            held.Section = InventoryScanner.SectionOf(held.Id);

            // Bags and saddlebags carry the character's name in their id, so a record
            // written before owners were kept can say whose it is without being re-read.
            // A retainer cannot: it is keyed by the retainer's name, which says nothing
            // about who hired them, so those wait until the next visit to a bell.
            if (string.IsNullOrWhiteSpace(held.Owner))
                held.Owner = InventoryScanner.OwnerFromId(held.Id);
        }

        // The Free Company chest is no longer tracked. It is a shared container that
        // anyone in the company can empty, so a record of it went stale the moment
        // someone else moved something - and it was never this plugin's job anyway.
        var chests = Config.Holdings.Keys
                           .Where(k => k == "fc" || k.StartsWith("fc.", StringComparison.Ordinal))
                           .ToList();

        foreach (var key in chests) Config.Holdings.Remove(key);

        if (Config.DedupeWatched())
            Log.Information("[FMF] Removed repeated entries from the remembered list.");

        Config.Save(PluginInterface);

        main = new MainWindow(this);
        windows.AddWindow(main);

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += Toggle;
        Framework.Update += OnUpdate;

        // The poll below cannot see a change that happens in the last moment before the
        // window shuts - move an item and close immediately and the containers are gone
        // before the next tick. The close itself is an event, so take the final reading
        // from that instead of hoping a tick lands in time.
        foreach (var name in HousingUi.Candidates)
            AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, name, OnHousingClosed);

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
        AddonLifecycle.UnregisterListener(OnHousingClosed);
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
        // The text has to be handed to the window: opening it and announcing a search was
        // all this did, with the box left empty.
        main.SearchFor(a);
    }

    // ---------- automatic capture ----------

    private void OnUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;

        // Nothing is read while the client is between areas.
        //
        // Working out who owns a property means calling a game function, and that function
        // crashed the game when it was called mid-transition: walking out of a house while
        // the watcher was running took it down. Zoning is also exactly when the containers
        // are being swapped out from underneath us, so there is nothing worth reading here
        // either. Everything resets, and the next property records itself afresh.
        if (Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51])
        {
            capturedThisSession = string.Empty;
            lastRawSignature = 0;
            pendingRaw = NoReading;
            pendingSince = DateTime.MinValue;
            visitStartedAt = DateTime.MinValue;
            recheck = false;
            retryNotBefore = DateTime.MinValue;
            lastSpoken.Clear();
            watchUntil = DateTime.MinValue;
            return;
        }

        if (now >= nextHousingCheck)
        {
            // Several times a second, not every two seconds. The check itself is now a
            // raw walk of the containers with no snapshot built, so this is cheap, and it
            // is the difference between a change being recorded at once and being recorded
            // whenever the next slow tick happens to come round.
            nextHousingCheck = now.AddMilliseconds(150);
            WatchHousingWindow();

            // The containers get the same treatment the housing window got: a raw sum
            // costs almost nothing, so it can be taken several times a second, and the
            // snapshot is only built on the tick something actually changed. A furnishing
            // dropped into your bag is now recorded in well under a second rather than
            // whenever the lazy sweep next came round.
            if (Config.AutoIndexBags)
            {
                var bagRaw = bags.RawSignature();

                if (bagRaw != lastBagSignature)
                {
                    lastBagSignature = bagRaw;
                    nextBagScan = DateTime.MinValue;
                }
            }
        }

        if (now >= nextBagScan)
        {
            // The signature above is what makes a change prompt; this is only the safety
            // net underneath it, for anything that changes without the slots changing.
            // Store announces on a change, so a sweep that finds nothing new is silent.
            nextBagScan = now.AddSeconds(10);

            if (Config.AutoIndexBags) ScanBags(force: false, announce: true);
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
    /// <summary>
    /// The last reading, taken as a Housing window is torn down.
    ///
    /// PreFinalize, not PostFinalize: the containers are still populated while the addon
    /// is being dismantled, and this is the only moment guaranteed to come after whatever
    /// the player did last. Several housing addons close together, so this can fire more
    /// than once - the contents check in CaptureHere makes the repeats free.
    ///
    /// The window is passed as open because it genuinely was: it is this addon closing
    /// that brought us here, and that is the evidence the gate exists to require.
    /// </summary>
    private void OnHousingClosed(AddonEvent type, AddonArgs args)
    {
        // Leaving a property closes its windows too, and that close arrives mid-transition
        // - the one moment the ownership check is known to take the client down. The poll
        // already refuses to run there; this is the same rule for the event.
        if (Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51])
            return;

        // One reading now, in case the change had already landed, and the watch stays
        // alive for a few seconds in case it had not.
        watchUntil = DateTime.UtcNow.AddSeconds(GraceSeconds);

        try { CaptureHere(manual: false, announce: false, windowIsOpen: true); }
        catch (Exception ex) { Log.Error(ex, "[FMF] Failed to record on closing the Housing window."); }
    }

    private void WatchHousingWindow()
    {
        var now = DateTime.UtcNow;
        var open = HousingUi.IsOpen(GameGui, out _);

        if (open)
        {
            watchUntil = now.AddSeconds(GraceSeconds);
            if (housingOpenedAt == DateTime.MinValue) housingOpenedAt = now;

            // Opening the Housing menu and stopping there records nothing, because the
            // furnishing containers are only filled by the Furnishings window itself.
            // Silence there looked like the plugin ignoring you.
            if (!hintShown && capturedThisSession.Length == 0
                && (now - housingOpenedAt).TotalSeconds >= HintSeconds
                && Config.AnnounceCaptures)
            {
                hintShown = true;
                ChatGui.Print("[FMF] Open the Furnishings window and toggle between Placed " +
                              "and Storeroom to record this property.");
            }
        }
        else
        {
            housingOpenedAt = DateTime.MinValue;
            hintShown = false;
        }

        if (!open && now >= watchUntil)
        {
            // Shut, and the grace period is spent. Opening it next time announces afresh.
            capturedThisSession = string.Empty;
            lastRawSignature = 0;
            pendingRaw = NoReading;
            pendingSince = DateTime.MinValue;
            visitStartedAt = DateTime.MinValue;
            recheck = false;
            retryNotBefore = DateTime.MinValue;
            lastSpoken.Clear();
            return;
        }

        // Order matters here, and it is the whole lesson of the crash.
        //
        // Identifying the property calls into the game. The container walk below does not
        // - it only reads memory the game already filled in. So the cheap one runs first
        // and gates the expensive one: at 150ms a tick, the game function is now touched
        // only when something has actually moved, which is rarer than the two-second poll
        // this replaced ever was.
        var raw = HousingReader.RawSignature(HousingReader.IsIndoors());
        var moved = raw != lastRawSignature;

        if (!moved && capturedThisSession.Length > 0 && !recheck) return;

        // Nothing has moved, so this is a second look at a reading that was refused or put
        // off. Worth taking, but not seven times a second.
        if (!moved && now < retryNotBefore) return;

        // Seen once is not seen. Act only on a reading that is still the same reading a
        // tick later, so a set of containers caught mid-swap is never written down or
        // announced. This costs 150ms on a real change, which is not noticeable, and it
        // sits above the identification call below so a property in flux is also not
        // paying for a game function it is going to throw away.
        if (raw != pendingRaw)
        {
            if (pendingSince != DateTime.MinValue)
                Log.Debug("[FMF] containers changed after " +
                          $"{(now - pendingSince).TotalMilliseconds:N0}ms; waiting again.");

            pendingRaw = raw;
            pendingSince = now;
            return;
        }

        // Arriving is the dangerous moment; decorating is not. Only the first reading of a
        // visit pays the full wait.
        if (capturedThisSession.Length == 0 &&
            (now - pendingSince).TotalSeconds < FirstReadingSettle)
            return;

        var here = HousingReader.CurrentKeyId(housingWindowOpen: true);

        // The clock runs from the first tick that could have read something, not from the
        // addon appearing, so a property that takes a moment to identify itself still gets
        // its full settling time.
        if (here.Length > 0 && visitStartedAt == DateTime.MinValue)
            visitStartedAt = now;

        if (here.Length == 0)
        {
            // Remember what we saw anyway, so an unidentifiable property is not re-asked
            // about on every single tick.
            lastRawSignature = raw;
            retryNotBefore = now.AddSeconds(RetrySeconds);
            return;
        }

        // In the grace period the window is already gone, so "this property" is only still
        // true while we have not moved. If it changed, the grace is over.
        if (!open && here != capturedThisSession)
        {
            watchUntil = DateTime.MinValue;
            capturedThisSession = string.Empty;
            lastRawSignature = 0;
            pendingRaw = NoReading;
            pendingSince = DateTime.MinValue;
            visitStartedAt = DateTime.MinValue;
            recheck = false;
            retryNotBefore = DateTime.MinValue;
            lastSpoken.Clear();
            return;
        }

        // The original gate re-recorded only when the number of loaded containers ROSE,
        // which caught the storeroom arriving late and nothing else: taking an item out of
        // the storeroom leaves the same pages loaded, so the index went on calling it
        // stored until the storeroom was closed and opened again. The raw comparison above
        // replaces that.
        var first = here != capturedThisSession;
        lastRawSignature = raw;

        // Only treat the property as announced once a reading has actually stored. The
        // first read after a window opens often lands before the containers have filled;
        // marking it done there meant the successful read a moment later counted as a
        // repeat and said nothing - which is why "Confirm each recording in chat" could
        // be ticked and stay silent.
        var settled = visitStartedAt != DateTime.MinValue &&
                      now - visitStartedAt >= TimeSpan.FromSeconds(SettleSeconds);

        if (!CaptureHere(manual: false, announce: first, windowIsOpen: true, settled: settled))
        {
            retryNotBefore = now.AddSeconds(RetrySeconds);
            return;
        }

        recheck = false;
        retryNotBefore = DateTime.MinValue;
        capturedThisSession = here;
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
        {
            snap.CustomName = previous.CustomName;

            // A reading taken where these could not be resolved must not blank ones that
            // were resolved before.
            if (string.IsNullOrWhiteSpace(snap.Owner)) snap.Owner = previous.Owner;
            if (string.IsNullOrWhiteSpace(snap.FreeCompany)) snap.FreeCompany = previous.FreeCompany;
        }

        Config.Properties[snap.Key.Id] = snap;
        Config.Save(PluginInterface);
    }

    /// <summary>
    /// Records the property you are standing in. Only ever called because you asked.
    /// </summary>
    public bool CaptureHere(bool manual) => CaptureHere(manual, announce: true);

    /// <param name="announce">
    /// Whether an automatic capture may say so in chat. False for the repeated reads taken
    /// while a Housing window sits open: the index still updates on every change, but a
    /// line per item moved while decorating is noise nobody wants.
    /// </param>
    /// <param name="settled">
    /// False while the game may still be filling the containers for a window that has only
    /// just opened. A half-read taken in that window is saved but not announced, and not
    /// counted as this visit's reading, so the whole one a moment later is the one that
    /// speaks.
    /// </param>
    private bool CaptureHere(bool manual, bool announce, bool? windowIsOpen = null,
                             bool settled = true)
    {
        // The containers are only trustworthy for this property once the game has opened a
        // housing window for it - reading them in a Free Company chamber otherwise
        // returned the hall's furnishings. It doubles as the access test, since the game
        // opens one only where you may edit.
        var windowOpen = windowIsOpen ?? HousingUi.IsOpen(GameGui, out _);

        // A shared estate is somebody else's. Off by default, and walking into one then
        // also clears anything recorded about it before that rule existed.
        //
        // First, because the general check below refuses a shared estate too and returns -
        // sitting after it, this never ran and those old records were never cleared.
        if (!Config.RecordSharedEstates && HousingReader.IsSharedEstate())
        {
            var id = HousingReader.CurrentKeyId(windowOpen);
            if (id.Length > 0 && Config.Properties.Remove(id)) Config.Save(PluginInterface);

            if (manual)
                ChatGui.PrintError("[FMF] This is a shared estate, and \"Record estates you " +
                                   "have editing rights on\" is off in Settings.");
            return false;
        }

        if (!HousingReader.CanIndexHere(windowOpen, out var why, Config.RecordSharedEstates))
        {
            if (manual) ChatGui.PrintError($"[FMF] Not recording here: {why}.");
            return false;
        }

        if (!windowOpen)
        {
            if (manual)
                ChatGui.PrintError("[FMF] Open the Housing menu (and the Storeroom) first, " +
                                   "then run this again.");
            return false;
        }

        var snap = housing.Capture(windowOpen, Config.RecordSharedEstates);
        if (snap == null)
        {
            if (manual)
                ChatGui.PrintError("[FMF] Could not read this property. Run /fmf dump and send " +
                                   "the [FMF] lines.");
            return false;
        }

        // The storeroom reports itself loaded a moment before it is filled, so a read can
        // land on an empty one and look like an emptied one. One empty read is therefore
        // not believed - but one that is still empty a couple of seconds later is. This
        // used to refuse every time, on the theory that a later read would "record its
        // zero honestly"; no later read was ever treated differently, so taking the last
        // thing out of a storeroom left the index listing the old contents for good.
        var readsEmptied = Config.Properties.TryGetValue(snap.Key.Id, out var before)
            && snap.StoreroomLoaded && before.StoreroomLoaded
            && snap.Pieces.Count(x => !SpotNames.IsPlaced(x.Where)) == 0
            && before.Pieces.Count(x => !SpotNames.IsPlaced(x.Where)) > 0
            && !manual;

        if (!readsEmptied)
        {
            emptyStoreKey = string.Empty;
        }
        else
        {
            var nowUtc = DateTime.UtcNow;

            if (emptyStoreKey != snap.Key.Id)
            {
                emptyStoreKey = snap.Key.Id;
                emptyStoreSince = nowUtc;
            }

            if ((nowUtc - emptyStoreSince).TotalSeconds < EmptyStoreroomSettle)
            {
                // Ask the watcher back: the containers will not change again to prompt it.
                recheck = true;
                Log.Debug($"[FMF] {snap.Address}: storeroom read back empty; waiting to see " +
                          "whether it stays that way.");
                return false;
            }

            emptyStoreKey = string.Empty;
            Log.Debug($"[FMF] {snap.Address}: storeroom has stayed empty; recording it as emptied.");
        }

        // A read that caught none of the placed containers says nothing about this house,
        // and must never replace one that did.
        if (Config.Properties.TryGetValue(snap.Key.Id, out var had)
            && !snap.PlacedLoaded && had.PlacedLoaded)
        {
            // Only when you asked. Automatically, this is simply the first read landing
            // before the containers have finished filling - outdoors especially - and the
            // next tick picks them up a moment later. Saying it out loud made a routine
            // wait look like a failure.
            Log.Debug($"[FMF] {snap.Address}: placed containers not loaded yet; keeping the " +
                      "existing record.");

            if (manual)
                ChatGui.PrintError("[FMF] The furnishings here have not loaded. Open the Housing " +
                                   "menu and try again - the existing record has been kept.");
            return false;
        }

        var cap = Limits(snap);
        var placed = snap.Pieces.Count(x => SpotNames.IsPlaced(x.Where));
        var stored = snap.Pieces.Count - placed;

        if (placed > cap.Placed || stored > cap.Stored)
        {
            var refusal = $"The game returned {placed:N0} placed and {stored:N0} stored, but " +
                          $"this property holds at most {cap.Placed}/{cap.Stored}. That is " +
                          "another estate's contents, so nothing was recorded.";

            // Once per reading, not once per look. The watcher comes back to a refused
            // property, and the same refusal is not news the second time.
            var alreadySaid = lastSpoken.TryGetValue(snap.Key.Id, out var said) && said == refusal;

            if (manual || !alreadySaid)
            {
                lastSpoken[snap.Key.Id] = refusal;

                Log.Warning($"[FMF] Refused {placed} placed / {stored} stored at {snap.Address}; " +
                            $"{snap.Estate?.ToString() ?? "this property"} holds at most " +
                            $"{cap.Placed}/{cap.Stored}.");

                if (manual || announce) ChatGui.PrintError($"[FMF] {refusal}");
            }

            return false;
        }

        // Nothing has moved since the last read, so there is nothing to WRITE. This is
        // what makes a read per tick cheap enough to do while the window is open.
        //
        // Deliberately not a reason to stay quiet, though. Walking into a property you
        // have not touched since your last visit is the ordinary case, and skipping the
        // confirmation there meant "Confirm each recording in chat" looked broken: the
        // Free Company chest spoke because a piece had moved, and the estate beside it
        // said nothing because nothing had.
        Config.Properties.TryGetValue(snap.Key.Id, out var existing);

        var unchanged = !manual && existing != null && Signature(existing) == Signature(snap);

        // Either half arriving turns a part-record into a whole one. That is worth saying
        // even though it is not the first read of the visit - it is the moment the "NOT
        // loaded" warning stops being true, and staying silent about it left you watching
        // a window and wondering whether anything had happened.
        var completed = existing != null &&
                        ((!existing.PlacedLoaded && snap.PlacedLoaded) ||
                         (!existing.StoreroomLoaded && snap.StoreroomLoaded));

        if (!unchanged) Save(snap);

        // Saved, but not spoken for and not this visit's reading yet. Returning false here
        // leaves the watcher looking, so the tick that completes the read is the one that
        // reports it - which is why you no longer get a "NOT loaded" line a half-second
        // before the real count.
        var whole = snap.PlacedLoaded && snap.StoreroomLoaded;
        if (!manual && !whole && !settled) return false;

        // A typed command always answers; the automatic one can be silenced, and the
        // repeated reads taken while the window sits open never speak at all - announce is
        // only true for the first successful read after the window opened.
        if (manual || ((announce || completed) && Config.AnnounceCaptures))
            Say(snap.Key.Id, snap.Label, Describe(snap), manual);

        return true;
    }

    /// <summary>
    /// A cheap value that changes when a property's contents do.
    ///
    /// Order-independent, because the game does not promise to hand the slots back in the
    /// same order twice and a reshuffle is not a change worth rewriting the record for.
    /// </summary>
    private static long Signature(PropertySnapshot snap) => Signature(snap.Pieces);

    private static long Signature(List<Piece> pieces)
    {
        long sig = pieces.Count;

        foreach (var p in pieces)
            sig += p.ItemId * 31L + (long)p.Where * 7L + p.Quantity + p.Stain;

        return sig;
    }

    private void ScanBags(bool force) => ScanBags(force, announce: true);

    /// <param name="announce">
    /// False for the rapid re-reads taken just after a piece moved. Those are the same
    /// containers being watched settle, not news.
    /// </param>
    /// <summary>
    /// Files this character's retainers under their name.
    ///
    /// A retainer is stored under the RETAINER's name, which says nothing about who hired
    /// them - so records written before owners were kept sat under "Unknown", and would
    /// have stayed there until each was next opened at a bell. The game knows the answer
    /// without anyone travelling: the retainer list belongs to the logged-in character, so
    /// every name on it is theirs.
    ///
    /// Once per character per session; the list does not change while you play.
    /// </summary>
    private void ClaimRetainers(string who)
    {
        if (who == claimedRetainersFor) return;

        try
        {
            var manager = RetainerManager.Instance();
            if (manager == null) return;

            var changed = false;
            var count = manager->GetRetainerCount();

            // The list is empty until the game has sent it, which is not at login. Marking
            // the character done on an empty list meant the one attempt of the session was
            // spent before there was anything to read.
            if (count == 0) return;
            claimedRetainersFor = who;

            for (uint i = 0; i < count; i++)
            {
                var retainer = manager->GetRetainerBySortedIndex(i);
                if (retainer == null) continue;

                var id = $"retainer.{retainer->NameString}";

                if (!Config.Holdings.TryGetValue(id, out var held)) continue;
                if (held.Owner == who) continue;

                held.Owner = who;
                changed = true;
            }

            if (changed)
            {
                Log.Information($"[FMF] Filed {who}'s retainers under their name.");
                Config.Save(PluginInterface);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[FMF] Failed to read the retainer list.");
        }
    }

    private string claimedRetainersFor = string.Empty;

    private void ScanBags(bool force, bool announce)
    {
        var who = LocalPlayerName;
        if (string.IsNullOrEmpty(who)) return;

        ClaimRetainers(who);

        // Which company this character is in, so the window can file the hall under
        // everyone who belongs to it.
        var company = HousingReader.FreeCompanyName();
        if (company.Length > 0 &&
            (!Config.CompanyOf.TryGetValue(who, out var known) || known != company))
        {
            Config.CompanyOf[who] = company;
            Config.Save(PluginInterface);
        }

        Store(bags.Bag(who), announce);
        Store(bags.Saddlebag(who), announce);
        Store(bags.Retainer(who), announce);

        if (force)
            ChatGui.Print($"[FMF] Scanned bags, saddlebag and retainers - " +
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

        // Both of these can only ever be read while a Housing window is open, because that
        // is the only state a capture happens in. Telling you to open one was wrong every
        // time it appeared; what is actually missing is a look at the other tab.
        if (!snap.PlacedLoaded || !snap.StoreroomLoaded)
            line += " Open Housing >> Furnishings and toggle between Placed and Storeroom " +
                    "to record the rest.";

        return line;
    }

    /// <summary>
    /// Stores a container snapshot, and says so when its contents actually changed.
    ///
    /// Containers are rescanned every few seconds, so announcing every scan would be
    /// constant noise. Announcing only a change means opening a retainer confirms itself
    /// and then goes quiet, which is the only moment the confirmation is worth anything.
    /// </summary>
    private void Store(HoldingSnapshot? snap, bool announce = true)
    {
        if (snap == null) return;

        var changed = !Config.Holdings.TryGetValue(snap.Id, out var had)
                      || Signature(had.Pieces) != Signature(snap.Pieces);

        if (!changed)
        {
            // Nothing moved, so there is nothing to write. This used to save regardless -
            // four full serialisations of the entire index every ten seconds, for ever,
            // whether or not the window was even open. Each one builds a multi-megabyte
            // string, and strings that size go on the large object heap, which is not
            // compacted and only swept on a generation-two collection. That is where a
            // tester's several hundred megabytes went.
            had!.TakenUtc = snap.TakenUtc;
            return;
        }

        Config.Holdings[snap.Id] = snap;
        Config.Save(PluginInterface);

        if (!announce || !Config.AnnounceCaptures) return;

        var n = snap.Pieces.Count;
        // "0 furnishings recorded" reads like the read failed. It did not - there simply
        // were none, which is a different and much less alarming thing to be told.
        Say(snap.Id, snap.DisplayName, n == 0
            ? "checked, no furnishings in it."
            : $"{n:N0} furnishing{(n == 1 ? "" : "s")} recorded.", force: false);
    }

    /// <summary>
    /// Prints a line, unless it is word for word the last line printed about this same
    /// thing. A command you typed always answers, because silence in reply to a command
    /// is indistinguishable from the command not working.
    /// </summary>
    private void Say(string key, string subject, string line, bool force)
    {
        if (!force && lastSpoken.TryGetValue(key, out var said) && said == line) return;

        lastSpoken[key] = line;
        ChatGui.Print($"[FMF] {subject}: {line}");
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

        foreach (var name in HousingUi.Diagnostics)
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
            // Every housing container, not a hand-picked four.
            //
            // The outdoor storeroom is not turning up where it is expected, and a list of
            // the containers we already believed in cannot show us one we do not. This
            // walks the whole enum, so whichever container actually holds the yard's
            // storage will appear here with a plausible number of items in it.
            var quiet = new List<string>();

            foreach (var t in Enum.GetValues<InventoryType>())
            {
                var name = t.ToString();
                if (!name.StartsWith("Housing", StringComparison.Ordinal)) continue;

                var c = inv->GetInventoryContainer(t);
                if (c == null) { quiet.Add($"{name}=null"); continue; }
                if (!c->IsLoaded) { quiet.Add($"{name}=unloaded"); continue; }

                var used = 0;
                for (var i = 0; i < c->Size; i++)
                {
                    var slot = c->GetInventorySlot(i);
                    if (slot != null && slot->ItemId != 0) used++;
                }

                Log.Information($"[FMF] {name}: LOADED size={c->Size} used={used}");
            }

            Log.Information($"[FMF] not loaded: {string.Join(", ", quiet)}");
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
