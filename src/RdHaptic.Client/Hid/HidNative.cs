using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RdHaptic.Client.Hid;

internal static class HidNative
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareReadWrite = 3;
    public const uint OpenExisting = 3;
    public const int HidpStatusSuccess = 0x00110000;
    public const uint CmGetDeviceInterfaceListPresent = 0;

    public enum ReportType { Input = 0, Output = 1, Feature = 2 }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidpValueCaps
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public byte HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        public ushort Reserved2a, Reserved2b, Reserved2c, Reserved2d, Reserved2e;
        public uint UnitsExp;
        public uint Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;
        // Range/NotRange union: when IsRange == 0, UsageMin holds NotRange.Usage.
        public ushort UsageMin;
        public ushort UsageMax;
        public ushort StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, int length, out int written, IntPtr overlapped);

    [DllImport("hid.dll")]
    public static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsed);

    [DllImport("hid.dll")]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HiddAttributes attributes);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool HidD_GetProductString(SafeFileHandle handle, char[] buffer, int length);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsed, out HidpCaps caps);

    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(ReportType type, [Out] HidpValueCaps[] caps, ref ushort length, IntPtr preparsed);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsageValue(ReportType type, ushort usagePage, ushort linkCollection, ushort usage, out uint value, IntPtr preparsed, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    public static extern int HidP_SetUsageValue(ReportType type, ushort usagePage, ushort linkCollection, ushort usage, uint value, IntPtr preparsed, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    public static extern int HidP_InitializeReportForID(ReportType type, byte reportId, IntPtr preparsed, byte[] report, uint reportLength);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_List_Size(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_List(ref Guid interfaceClass, string? deviceId, char[] buffer, uint length, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct HiddAttributes
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    public static IEnumerable<string> HidInterfacePaths()
    {
        HidD_GetHidGuid(out var guid);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_Size(out var len, ref guid, null, CmGetDeviceInterfaceListPresent) != 0 || len <= 1)
                return [];
            var buffer = new char[len];
            // CR_BUFFER_SMALL (0x1A) means a device arrived between the two calls; retry.
            if (CM_Get_Device_Interface_List(ref guid, null, buffer, len, CmGetDeviceInterfaceListPresent) == 0)
                return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        return [];
    }
}
