using System;
using Dalamud.Configuration;

namespace EorzeanMarketMaster;

/// <summary>
/// Persisted to %APPDATA%\XIVLauncher\pluginConfigs\EorzeanMarketMaster\.
/// Scaffold only: this holds window state and nothing product-bearing. Strategies, Groups,
/// Mandates, guardrails and the sell space all land here in later tickets.
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary>Icon rail shows labels beside the icons when true.</summary>
    public bool RailExpanded { get; set; }

    /// <summary>Index into <see cref="Ui.MainWindow.SizePresets"/>.</summary>
    public int SizePreset { get; set; }

    /// <summary>Which rail entry the main window last had open.</summary>
    public int ActiveSection { get; set; }

    /// <summary>
    /// When the last ring-wide sweep began, carried across a restart.
    ///
    /// Persisted rather than held in memory because the fifteen-minute floor is a promise to
    /// somebody else's server, and a floor that forgets itself on reload is one a plugin reload
    /// steps straight over. Nothing here is a knob on the ceilings themselves - those have no
    /// setting anywhere, by construction.
    /// </summary>
    public DateTimeOffset? LastSweepStartedAt { get; set; }

    /// <summary>The Item the Scan section last had selected.</summary>
    public uint ScanItemId { get; set; } = 5;

    /// <summary>Whether the Scan section last had the HQ Ware of that Item selected.</summary>
    public bool ScanHighQuality { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
