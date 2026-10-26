using System.Text.Json;
using RdHaptic.Protocol;

namespace RdHaptic.Agent;

/// <summary>Events the agent can pulse for; each maps to a waveform via <see cref="AgentConfig.Waveforms"/>.</summary>
public enum Trigger
{
    /// <summary>Dragged window reaches a side edge or corner snap zone.</summary>
    EdgeEnter,
    /// <summary>Dragged window reaches the top edge (maximize / Snap Layouts bar), or a resize reaches top/bottom (vertical maximize).</summary>
    TopEdgeEnter,
    SnapRelease,
    SnapBarZone,
    ResizeLimit,
    MonitorCross,
}

/// <summary>Agent settings from %APPDATA%\RdHaptic\agent.json (written with defaults on first run).</summary>
public sealed class AgentConfig
{
    /// <summary>Base intensity sent with every pulse, 0-100.</summary>
    public int Intensity { get; set; } = 100;

    /// <summary>Hover pulse when a dragged window's cursor enters a snap zone (edge/corner/top).</summary>
    public bool EdgeHover { get; set; } = true;

    /// <summary>Align pulse when a window is dropped into a snap zone or ends up snapped.</summary>
    public bool SnapAlign { get; set; } = true;

    /// <summary>Collide pulse when a resize hits the window's min/max size.</summary>
    public bool ResizeCollide { get; set; } = true;

    /// <summary>Hover pulse for each zone of the Snap Layouts bar the cursor moves onto while dragging.</summary>
    public bool SnapBarHover { get; set; } = true;

    /// <summary>Step pulse when a dragged window's cursor crosses onto another monitor.</summary>
    public bool MonitorCross { get; set; } = true;

    /// <summary>Distance (px) from a screen edge that counts as touching it.</summary>
    public int EdgePx { get; set; } = 2;

    /// <summary>Size (px) of the corner regions along an edge that snap to a quarter.</summary>
    public int CornerPx { get; set; } = 40;

    /// <summary>How far (px) the cursor must outrun the window edge during a resize to count as a collision.</summary>
    public int CollidePx { get; set; } = 12;

    /// <summary>How long (ms) a dragged edge must stay still before the cursor running past it counts as a collision.
    /// Raise it if slow-to-redraw apps still pulse mid-resize; lower it for a snappier pulse.</summary>
    public int CollideStallMs { get; set; } = 250;

    /// <summary>Cursor polling interval while a window is being moved or sized.</summary>
    public int PollMs { get; set; } = 8;

    /// <summary>Minimum gap between two pulses of the same waveform.</summary>
    public int CooldownMs { get; set; } = 120;

    /// <summary>
    /// Overrides for which waveform a trigger plays, by name (hover, collide, align, step, grow, success, error, click),
    /// e.g. {"TopEdgeEnter": "Align"}. Only overrides live here, so improved built-in defaults still reach existing
    /// installs; missing or unknown entries use the default for that trigger.
    /// </summary>
    public Dictionary<string, string> Waveforms { get; set; } = new();

    private static readonly Dictionary<Trigger, Waveform> DefaultWaveforms = new()
    {
        [Trigger.EdgeEnter] = Waveform.Hover,
        [Trigger.TopEdgeEnter] = Waveform.Collide, // closest match to the native top-edge pulse (by feel, Surface Mouse 2nd Ed.)
        [Trigger.SnapRelease] = Waveform.Align,
        [Trigger.SnapBarZone] = Waveform.Hover,
        [Trigger.ResizeLimit] = Waveform.Collide,
        [Trigger.MonitorCross] = Waveform.Step,
    };

    public Waveform WaveformFor(Trigger trigger) =>
        Waveforms.TryGetValue(trigger.ToString(), out var name) && WaveformNames.TryParse(name, out var w)
            ? w
            : DefaultWaveforms[trigger];

    /// <summary>Optional per-waveform scaling in percent, e.g. {"Hover": 60}. Empty = full strength, matching Windows.</summary>
    public Dictionary<string, int> WaveformIntensity { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip };

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdHaptic", "agent.json");

    public byte IntensityFor(Waveform waveform)
    {
        var pct = WaveformIntensity.TryGetValue(waveform.ToString(), out var p) ? p : 100;
        return (byte)Math.Clamp(Intensity * pct / 100, 0, 100);
    }

    public static AgentConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(FilePath), Json) ?? new AgentConfig();
                // Rewrite so settings added in newer versions show up in the file.
                File.WriteAllText(FilePath, JsonSerializer.Serialize(config, Json));
                return config;
            }
            var defaults = new AgentConfig();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(defaults, Json));
            return defaults;
        }
        catch (Exception ex)
        {
            Log.Error($"Bad config {FilePath}; using defaults", ex);
            return new AgentConfig();
        }
    }

    /// <summary>Re-applies agent.json to this instance whenever the file is saved, so edits apply without a restart.</summary>
    public IDisposable Watch()
    {
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(FilePath)!, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        Timer? debounce = null;
        void Changed(object? _, FileSystemEventArgs __)
        {
            debounce?.Dispose();
            debounce = new Timer(_ => Reload(), null, 300, Timeout.Infinite);
        }
        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Renamed += (s, e) => Changed(s, e);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void Reload()
    {
        try
        {
            var fresh = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(FilePath), Json);
            if (fresh is null)
                return;
            foreach (var prop in typeof(AgentConfig).GetProperties().Where(p => p.CanWrite))
                prop.SetValue(this, prop.GetValue(fresh));
            Log.Info($"Config reloaded: {JsonSerializer.Serialize(this)}");
        }
        catch (Exception ex)
        {
            Log.Error($"Config reload failed; keeping previous settings", ex);
        }
    }
}
