using Microsoft.Win32;
using RdHaptic.Protocol;

namespace RdHaptic.Client;

/// <summary>Client settings from HKCU\Software\RdHaptic (all optional).</summary>
public sealed class ClientSettings
{
    public const string KeyPath = @"Software\RdHaptic";

    public bool Enabled { get; init; } = true;
    public int IntensityScalePct { get; init; } = 100;
    public IReadOnlySet<Waveform> Muted { get; init; } = new HashSet<Waveform>();

    public bool IsMuted(Waveform waveform) => Muted.Contains(waveform);

    public static ClientSettings Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null)
                return new ClientSettings();
            var muted = new HashSet<Waveform>();
            if (key.GetValue("Muted") is string mutedText)
                foreach (var name in mutedText.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
                    if (WaveformNames.TryParse(name, out var w))
                        muted.Add(w);
            return new ClientSettings
            {
                Enabled = key.GetValue("Enabled") is not int enabled || enabled != 0,
                IntensityScalePct = key.GetValue("IntensityScale") is int scale ? Math.Clamp(scale, 0, 100) : 100,
                Muted = muted,
            };
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read client settings", ex);
            return new ClientSettings();
        }
    }
}
