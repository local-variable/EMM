using System;
using System.IO;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Graph;
using EorzeanMarketMaster.Ingest;
using EorzeanMarketMaster.Probe;
using EorzeanMarketMaster.Store;
using EorzeanMarketMaster.Ui;

namespace EorzeanMarketMaster;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    // Services the #18 observation harness needs. They are read-only surfaces; nothing here writes
    // to the game.
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    private const string CommandName = "/emm";

    public Configuration Configuration { get; init; }

    /// <summary>One line for the status strip: what EMM can see from where it is standing.</summary>
    public string EnvironmentSummary { get; private set; } = string.Empty;

    private readonly WindowSystem windowSystem = new("EorzeanMarketMaster");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly SelfTest selfTest;
    private readonly LiveProbe probe;
    private readonly AutoRetainerWatch autoRetainer;

    /// <summary>
    /// What EMM observes, kept somewhere it survives a restart. Null where the store could not be
    /// opened, which hobbles EMM rather than failing its load - see <see cref="StoreHost"/>.
    /// </summary>
    private readonly StoreHost storeHost;

    /// <summary>
    /// Aggregator ingest and the state the Scan section draws. Null where the store could not be
    /// opened - there is nowhere to put an observation, so there is nothing to fetch.
    /// </summary>
    internal ScanHost? Scan { get; }

    /// <summary>
    /// The History the Pricing section draws. Null where the store could not be opened - the
    /// series is built from stored Sales, so with no store there is nothing to build one from.
    /// </summary>
    internal GraphHost? Chart { get; }

    /// <summary>
    /// Which Ware every surface is looking at. One object rather than one per section, so a
    /// Freshness and a graph on screen together are always about the same Ware.
    /// </summary>
    private readonly Selection selection;

    /// <summary>
    /// The decision seam. Every decision EMM makes will be made behind this one call, and this
    /// side of the boundary only ever fills a <see cref="WorldState"/> or applies an
    /// <see cref="Outcome"/>. It returns an empty Outcome today; the adapters that fill the state
    /// arrive with the tickets that follow.
    /// </summary>
    private readonly DecisionEngine engine = new();

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // AutoRetainerApi reads its plugin interface off ECommons' static Svc, so ECommons has to be
        // initialised before one can be constructed. No modules: EMM uses none of them.
        ECommons.ECommonsMain.Init(PluginInterface, this);

        // A dev-loaded plugin reads its icon from images/icon.png beside the DLL. The same file
        // backs the rail logo, so the UI and the installer entry cannot drift apart.
        var iconPath = Path.Combine(
            PluginInterface.AssemblyLocation.Directory?.FullName!, "images", "icon.png");

        mainWindow = new MainWindow(this, iconPath);
        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);
        selfTest = new SelfTest(mainWindow, Configuration);

        // The #18 live-session harness. Observation only; see LiveProbe's class comment.
        probe = new LiveProbe();
        autoRetainer = new AutoRetainerWatch(probe, probe.WriteEntry);

        // The store. Opened here rather than in a field initialiser on purpose: field
        // initialisers run above the top of this constructor, so a throw from one of them cannot
        // be caught here at all. StoreHost reports failure instead of throwing, but the ordering
        // is what makes that reliable rather than lucky.
        storeHost = StoreHost.Open(
            PluginInterface.AssemblyLocation.Directory?.FullName!,
            PluginInterface.ConfigDirectory.FullName);

        Log.Information("EMM {Status}", storeHost.Status);

        selection = new Selection(Configuration);

        Scan = storeHost.Store is null
            ? null
            : new ScanHost(storeHost.Store, storeHost.Gate, Configuration, selection);
        Chart = storeHost.Store is null
            ? null
            : new GraphHost(storeHost.Store, storeHost.Gate, selection);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Eorzean Market Master. /emm config opens its settings, /emm selftest checks the UI, /emm probe drives the live-session harness.",
        });

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        EnvironmentSummary = DescribeEnvironment();
        Log.Information("EMM scaffold loaded. {Summary}", EnvironmentSummary);

        // Same reasoning as DescribeEnvironment's vendored-dependency check: a project reference
        // that resolves at build time proves nothing about whether the packager laid the assembly
        // down beside the plugin. Calling across the seam once at load is what shows it did.
        // Log only - this is a developer-facing fact, not a line for the status strip.
        //
        // What the catch does and does not cover, stated rather than assumed. It does NOT catch a
        // missing EorzeanMarketMaster.Core.dll: the `engine` field initialiser runs at the top of
        // this constructor, before this block, and a genuinely absent assembly fails the load
        // there. That is the right outcome anyway - EMM cannot decide anything without its
        // decision layer - and Dalamud reports it plainly. What the catch does cover is Evaluate
        // itself throwing, which it cannot today and will be able to as the tickets below fill it
        // in. A startup diagnostic that takes the whole plugin down with it is a bad trade.
        try
        {
            var startup = engine.Evaluate(WorldState.Empty);
            Log.Information(
                "EMM Core linked - Evaluate returned {Proposals} Proposals, {Holds} Holds, {Notices} Notices, {Estimates} Estimates",
                startup.Proposals.Count, startup.Holds.Count, startup.Notices.Count, startup.Estimates.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "EMM Core FAILED to load - the decision seam is unreachable");
        }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        configWindow.Dispose();

        // AutoRetainer first: if a postprocess window is somehow open, it has to be closed before
        // the subscription that can close it goes away.
        autoRetainer.Dispose();
        probe.Dispose();

        // Before the store, and it matters as much as the ordering below: ScanHost may have a
        // refresh in flight holding the store's connection, and disposing that connection out from
        // under a write is how a database gets a torn page. Disposing the host waits for it.
        Scan?.Dispose();

        // Before ECommons, and it matters: disposing the store checkpoints its write-ahead log and
        // releases the file handle. A handle still open after unload is what makes an in-place
        // plugin update fail while the game is running.
        storeHost.Dispose();

        ECommons.ECommonsMain.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    /// <summary>The self-test drives the window across frames, so it ticks ahead of the draw.</summary>
    private void DrawUi()
    {
        selfTest.Tick();

        // Only while the window is open. The Scan section's figures are hours old by nature, so
        // there is nothing to keep warm - and a query a second against the store for a window
        // nobody is looking at is a cost with no reader.
        if (mainWindow.IsOpen)
        {
            var now = DateTimeOffset.UtcNow;
            WorldId? world = PlayerState.IsLoaded ? new WorldId(PlayerState.HomeWorld.RowId) : null;

            Scan?.Tick(now, world);
            Chart?.Tick(now, world);
        }

        windowSystem.Draw();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        var verb = trimmed.Split(' ', 2)[0].ToLowerInvariant();

        switch (verb)
        {
            case "config":
                ToggleConfigUi();
                break;
            case "selftest":
                selfTest.Start();
                break;
            case "probe":
                OnProbeCommand(trimmed.Length > verb.Length ? trimmed[(verb.Length + 1)..].Trim() : string.Empty);
                break;
            default:
                ToggleMainUi();
                break;
        }
    }

    /// <summary>
    /// The #18 harness's controls. Every one of these is a read except `arm` and `suppress`, and
    /// those two are spelled out rather than folded into a general switch precisely because they
    /// are the ones that touch the player's retainer run.
    /// </summary>
    private void OnProbeCommand(string args)
    {
        var parts = args.Split(' ', 2);
        var verb = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (verb)
        {
            case "retainers":
                probe.DumpRetainers("command");
                Echo("retainer summaries written to probe.log");
                break;
            case "market":
                probe.DumpMarket("command");
                Echo("market container written to probe.log");
                break;
            case "logmsg":
                probe.DumpLogMessages();
                Echo("LogMessage rows written to probe.log");
                break;
            case "ar":
                autoRetainer.Attach();
                Echo(autoRetainer.Status());
                break;
            case "arm":
                if (!autoRetainer.Attached)
                {
                    Echo("attach first: /emm probe ar");
                    break;
                }

                autoRetainer.Arm();
                Echo("ARMED — EMM will take a postprocess turn on the next retainer, read it, and hand back immediately.");
                break;
            case "disarm":
                autoRetainer.Disarm();
                Echo("disarmed");
                break;
            case "suppress":
                var wanted = rest.Equals("on", StringComparison.OrdinalIgnoreCase);
                Echo(autoRetainer.SetSuppressed(wanted)
                    ? $"AutoRetainer suppressed={wanted}"
                    : "could not set suppression (attach first: /emm probe ar)");
                break;
            case "note":
                probe.Note(rest);
                Echo($"noted: {rest}");
                break;
            case "chat":
                var chatOn = !rest.Equals("off", StringComparison.OrdinalIgnoreCase);
                probe.SetChatCapture(chatOn, 10);
                Echo(chatOn
                    ? "capturing ALL chat and LogMessage rows for 10 min - say something to prove the listener is alive"
                    : "chat capture off");
                break;
            default:
                Echo($"probe log: {probe.LogPath}");
                Echo(probe.LivenessSummary());
                Echo(autoRetainer.Status());
                Echo("retainers | market | logmsg | ar | arm | disarm | suppress on|off | chat on|off | note <text>");
                break;
        }
    }

    private static void Echo(string message) => ChatGui.Print($"[EMM probe] {message}");

    private void ToggleConfigUi() => configWindow.Toggle();

    private void ToggleMainUi() => mainWindow.Toggle();

    /// <summary>
    /// Proves the vendored dependencies resolve at runtime, and reports what EMM can see.
    ///
    /// This deliberately only loads types. It does NOT construct AutoRetainerApi, register for
    /// postprocess, or call anything on AutoRetainer:
    ///
    ///   - AutoRetainer is busy on first open and must be left to finish before anything else
    ///     touches the retainer flow, or it gets interrupted. EMM's window is the postprocess
    ///     callback AutoRetainer hands over deliberately, and that is the only place EMM may act.
    ///   - Registering for postprocess makes AutoRetainer block on
    ///     FinishRetainerPostProcess() with timeLimitMS: int.MaxValue, and suppresses its own
    ///     bailout watchdog for the whole of that window. A scaffold that registered and never
    ///     finished would hang AutoRetainer outright.
    ///
    /// Subscribing is the write-path ticket's job, not the scaffold's.
    /// </summary>
    private static string DescribeEnvironment()
    {
        string vendored;
        try
        {
            var apiAssembly = typeof(AutoRetainerAPI.AutoRetainerApi).Assembly.GetName();
            var ecommons = typeof(ECommons.ECommonsMain).Assembly.GetName();
            vendored = $"AutoRetainerAPI {apiAssembly.Version} + ECommons {ecommons.Version} linked";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Vendored dependencies failed to load");
            vendored = "vendored dependencies FAILED to load";
        }

        var autoRetainer = PluginInterface.InstalledPlugins
            .FirstOrDefault(p => p.InternalName == "AutoRetainer");
        var provider = autoRetainer is null
            ? "AutoRetainer not installed"
            : $"AutoRetainer {autoRetainer.Version}{(autoRetainer.IsLoaded ? string.Empty : " (not loaded)")}";

        // The home World is EMM's market Scope, so that is the one worth reporting.
        var world = PlayerState.IsLoaded
            ? $"home World {PlayerState.HomeWorld.Value.Name.ExtractText()}"
            : "not logged in";

        return $"Scaffold — {vendored}. {provider}. {world}.";
    }
}
