using System;
using System.IO;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
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

    private const string CommandName = "/emm";

    public Configuration Configuration { get; init; }

    /// <summary>One line for the status strip: what EMM can see from where it is standing.</summary>
    public string EnvironmentSummary { get; private set; } = string.Empty;

    private readonly WindowSystem windowSystem = new("EorzeanMarketMaster");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly SelfTest selfTest;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // A dev-loaded plugin reads its icon from images/icon.png beside the DLL. The same file
        // backs the rail logo, so the UI and the installer entry cannot drift apart.
        var iconPath = Path.Combine(
            PluginInterface.AssemblyLocation.Directory?.FullName!, "images", "icon.png");

        mainWindow = new MainWindow(this, iconPath);
        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);
        selfTest = new SelfTest(mainWindow, Configuration);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Eorzean Market Master. /emm config opens its settings, /emm selftest checks the UI.",
        });

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        EnvironmentSummary = DescribeEnvironment();
        Log.Information("EMM scaffold loaded. {Summary}", EnvironmentSummary);
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        configWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    /// <summary>The self-test drives the window across frames, so it ticks ahead of the draw.</summary>
    private void DrawUi()
    {
        selfTest.Tick();
        windowSystem.Draw();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "config":
                ToggleConfigUi();
                break;
            case "selftest":
                selfTest.Start();
                break;
            default:
                ToggleMainUi();
                break;
        }
    }

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
