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

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
