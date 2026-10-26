using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RdHaptic.Protocol;
using static RdHaptic.Client.Hid.HidNative;

namespace RdHaptic.Client.Hid;

/// <summary>
/// A HID SimpleHapticsController top-level collection (page 0x0E, usage 0x01), driven per the Windows
/// "Input Device Haptics Implementation Guide": feature report = waveform/duration lists, output report = manual trigger.
/// </summary>
public sealed class HidHapticDevice : IDisposable
{
    private const ushort HapticsPage = 0x0E;
    private const ushort OrdinalPage = 0x0A;
    private const ushort UsageSimpleHapticController = 0x01;
    private const ushort UsageWaveformList = 0x10;
    private const ushort UsageDurationList = 0x11;
    private const ushort UsageManualTrigger = 0x21;
    private const ushort UsageIntensity = 0x23;

    private readonly SafeFileHandle _handle;
    private readonly IntPtr _preparsed;
    private readonly int _outputLength;
    private readonly byte _outputReportId;
    private readonly ushort _triggerLink;
    private readonly ushort _intensityLink;
    private readonly Dictionary<ushort, byte> _ordinalByUsage = new();
    private readonly object _writeGate = new();
    private bool _disposed;

    public string Path { get; }
    public string Product { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public int IntensityMax { get; }
    public IReadOnlyList<WaveformInfo> Waveforms { get; }
    public IReadOnlyDictionary<ushort, byte> OrdinalByUsage => _ordinalByUsage;

    private HidHapticDevice(string path, SafeFileHandle handle, IntPtr preparsed, HidpCaps caps)
    {
        Path = path;
        _handle = handle;
        _preparsed = preparsed;
        _outputLength = caps.OutputReportByteLength;

        var attrs = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
        if (HidD_GetAttributes(handle, ref attrs))
        {
            VendorId = attrs.VendorID;
            ProductId = attrs.ProductID;
        }
        var name = new char[128];
        var product = HidD_GetProductString(handle, name, name.Length * 2) ? new string(name).TrimEnd('\0') : "";
        Product = product.Length > 0 ? product : "Haptic device";

        // Output report: Manual Trigger + Intensity.
        var outCaps = GetValueCaps(ReportType.Output, caps.NumberOutputValueCaps);
        var trigger = outCaps.FirstOrDefault(c => c.UsagePage == HapticsPage && c.UsageMin == UsageManualTrigger);
        var intensity = outCaps.FirstOrDefault(c => c.UsagePage == HapticsPage && c.UsageMin == UsageIntensity);
        if (trigger.UsagePage == 0 || intensity.UsagePage == 0)
            throw new NotSupportedException("Haptic collection has no Manual Trigger/Intensity output usages");
        _outputReportId = trigger.ReportID;
        _triggerLink = trigger.LinkCollection;
        _intensityLink = intensity.LinkCollection;
        IntensityMax = intensity.LogicalMax;

        Waveforms = ReadWaveformTable(caps);
    }

    /// <summary>Opens every present haptic collection. Collections that fail to open are skipped.</summary>
    public static List<HidHapticDevice> Enumerate()
    {
        var result = new List<HidHapticDevice>();
        foreach (var path in HidInterfacePaths())
        {
            var dev = TryOpen(path);
            if (dev is not null)
                result.Add(dev);
        }
        return result;
    }

    public static HidHapticDevice? OpenFirst()
    {
        HidHapticDevice? best = null;
        foreach (var dev in Enumerate())
        {
            if (best is null || dev.Waveforms.Count > best.Waveforms.Count)
            {
                best?.Dispose();
                best = dev;
            }
            else
            {
                dev.Dispose();
            }
        }
        return best;
    }

    private static HidHapticDevice? TryOpen(string path)
    {
        // Peek at the top-level usage with zero access first so we never hold handles on unrelated devices.
        using (var probe = CreateFile(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
        {
            if (probe.IsInvalid || !HidD_GetPreparsedData(probe, out var pp))
                return null;
            try
            {
                if (HidP_GetCaps(pp, out var c) != HidpStatusSuccess || c.UsagePage != HapticsPage || c.Usage != UsageSimpleHapticController)
                    return null;
            }
            finally { HidD_FreePreparsedData(pp); }
        }

        var handle = CreateFile(path, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Log.Error($"Cannot open haptic collection {path} (err {Marshal.GetLastWin32Error()})");
            return null;
        }
        if (!HidD_GetPreparsedData(handle, out var preparsed))
        {
            handle.Dispose();
            return null;
        }
        try
        {
            HidP_GetCaps(preparsed, out var caps);
            return new HidHapticDevice(path, handle, preparsed, caps);
        }
        catch (Exception ex)
        {
            Log.Error($"Haptic collection {path} unusable", ex);
            HidD_FreePreparsedData(preparsed);
            handle.Dispose();
            return null;
        }
    }

    private IReadOnlyList<WaveformInfo> ReadWaveformTable(HidpCaps caps)
    {
        var featCaps = GetValueCaps(ReportType.Feature, caps.NumberFeatureValueCaps);
        var list = featCaps.FirstOrDefault(c => c.UsagePage == OrdinalPage && c.LinkUsagePage == HapticsPage && c.LinkUsage == UsageWaveformList);
        var durations = featCaps.FirstOrDefault(c => c.UsagePage == OrdinalPage && c.LinkUsagePage == HapticsPage && c.LinkUsage == UsageDurationList);
        if (list.UsagePage == 0)
            throw new NotSupportedException("Haptic collection has no Waveform List feature report");

        var report = new byte[caps.FeatureReportByteLength];
        report[0] = list.ReportID;
        if (!HidD_GetFeature(_handle, report, report.Length))
            throw new IOException($"GET_FEATURE waveform list failed (err {Marshal.GetLastWin32Error()})");

        var result = new List<WaveformInfo>();
        var stop = list.IsRange != 0 ? list.UsageMax : list.UsageMin;
        for (ushort ordinal = list.UsageMin; ordinal <= stop; ordinal++)
        {
            if (HidP_GetUsageValue(ReportType.Feature, OrdinalPage, list.LinkCollection, ordinal, out var usage, _preparsed, report, (uint)report.Length) != HidpStatusSuccess)
                continue;
            // Firmware may return junk in unused slots (the Surface Mouse echoes durations), so honour the
            // declared logical range (0x1001..0x2FFF) rather than trusting every slot.
            if (usage < (uint)Math.Max(list.LogicalMin, (int)Waveform.None) || usage > (uint)list.LogicalMax
                || usage is (ushort)Waveform.None or (ushort)Waveform.Stop || _ordinalByUsage.ContainsKey((ushort)usage))
                continue;
            uint duration = 0;
            if (durations.UsagePage != 0)
                HidP_GetUsageValue(ReportType.Feature, OrdinalPage, durations.LinkCollection, ordinal, out duration, _preparsed, report, (uint)report.Length);
            _ordinalByUsage[(ushort)usage] = (byte)ordinal;
            result.Add(new WaveformInfo((ushort)usage, (ushort)duration));
        }
        return result;
    }

    private HidpValueCaps[] GetValueCaps(ReportType type, ushort count)
    {
        if (count == 0)
            return [];
        var caps = new HidpValueCaps[count];
        HidP_GetValueCaps(type, caps, ref count, _preparsed);
        return caps[..count];
    }

    public bool Supports(Waveform waveform) => _ordinalByUsage.ContainsKey((ushort)waveform);

    public ushort DurationMs(Waveform waveform) =>
        Waveforms.FirstOrDefault(w => w.Usage == (ushort)waveform).DurationMs;

    /// <summary>Plays a waveform at intensity 0-100%. Returns false if the write failed (device gone).</summary>
    public bool Play(Waveform waveform, byte intensityPct)
    {
        byte ordinal;
        if (waveform == Waveform.Stop)
            ordinal = 2;
        else if (!_ordinalByUsage.TryGetValue((ushort)waveform, out ordinal))
            return true;

        uint level = intensityPct == 0 || waveform == Waveform.Stop
            ? 0
            : (uint)Math.Clamp((int)Math.Round(intensityPct / 100.0 * IntensityMax), 1, IntensityMax);
        return WriteTrigger(ordinal, level);
    }

    private bool WriteTrigger(byte ordinal, uint level)
    {
        lock (_writeGate)
        {
            if (_disposed)
                return false;
            var report = new byte[_outputLength];
            if (HidP_InitializeReportForID(ReportType.Output, _outputReportId, _preparsed, report, (uint)report.Length) != HidpStatusSuccess
                || HidP_SetUsageValue(ReportType.Output, HapticsPage, _triggerLink, UsageManualTrigger, ordinal, _preparsed, report, (uint)report.Length) != HidpStatusSuccess
                || HidP_SetUsageValue(ReportType.Output, HapticsPage, _intensityLink, UsageIntensity, level, _preparsed, report, (uint)report.Length) != HidpStatusSuccess)
            {
                Log.Error("Failed to build haptic output report");
                return true; // not a device failure; don't trigger a reopen loop
            }
            if (WriteFile(_handle, report, report.Length, out _, IntPtr.Zero))
                return true;
            Log.Error($"Haptic write failed (err {Marshal.GetLastWin32Error()})");
            return false;
        }
    }

    public void Dispose()
    {
        lock (_writeGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            HidD_FreePreparsedData(_preparsed);
            _handle.Dispose();
        }
    }
}
