namespace RdHaptic.Protocol;

/// <summary>HID Haptics page (0x0E) waveform usages. These go over the wire; the client maps them to device ordinals.</summary>
public enum Waveform : ushort
{
    None = 0x1001,
    Stop = 0x1002,
    Click = 0x1003,
    Press = 0x1006,
    Release = 0x1007,
    Hover = 0x1008,
    Success = 0x1009,
    Error = 0x100A,
    Collide = 0x1012,
    Align = 0x1013,
    Step = 0x1014,
    Grow = 0x1015,
}

public static class WaveformNames
{
    public static bool TryParse(string text, out Waveform waveform)
    {
        if (Enum.TryParse(text, ignoreCase: true, out waveform) && Enum.IsDefined(waveform))
            return true;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ushort.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var raw))
        {
            waveform = (Waveform)raw;
            return true;
        }
        return false;
    }

    public static string Name(ushort usage) =>
        Enum.IsDefined((Waveform)usage) ? ((Waveform)usage).ToString() : $"0x{usage:X4}";
}
