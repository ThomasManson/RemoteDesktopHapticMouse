using System.Runtime.InteropServices;

namespace RdHaptic.Agent;

public static class Native
{
    // --- WTS virtual channels ---
    public const uint WtsCurrentSession = 0xFFFFFFFF;
    public const uint WtsChannelOptionDynamic = 0x00000001;
    public const uint WtsChannelOptionDynamicPriReal = 0x00000006;
    public const int WtsVirtualFileHandle = 1;
    public const uint ChannelFlagFirst = 0x01;
    public const uint ChannelFlagLast = 0x02;
    public const int ChannelPduHeaderSize = 8;
    public const int ChannelChunkLength = 1600;

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr WTSVirtualChannelOpenEx(uint sessionId, string virtualName, uint flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSVirtualChannelQuery(IntPtr channel, int virtualClass, out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSVirtualChannelClose(IntPtr channel);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr memory);

    // --- Overlapped file I/O on the channel handle ---
    public const uint ErrorIoPending = 997;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool ReadFile(IntPtr file, byte* buffer, int toRead, IntPtr read, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool WriteFile(IntPtr file, byte* buffer, int toWrite, IntPtr written, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool GetOverlappedResult(IntPtr file, NativeOverlapped* overlapped, out int transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool CancelIoEx(IntPtr file, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEvent(IntPtr security, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    public static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = -4;

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    public static extern bool AllocConsole();

    // --- WinEvents / window geometry ---
    public const uint EventSystemMoveSizeStart = 0x000A;
    public const uint EventSystemMoveSizeEnd = 0x000B;
    public const uint WinEventOutOfContext = 0x0000;
    public const uint WinEventSkipOwnProcess = 0x0002;
    public const int ObjIdWindow = 0;
    public const uint MonitorDefaultToNull = 0;
    public const uint MonitorDefaultToNearest = 2;
    public const uint WmNcHitTest = 0x0084;
    public const uint SmtoAbortIfHung = 0x0002;
    public const uint SpiGetDragFullWindows = 0x0026;
    public const uint SpiGetWinArranging = 0x0082;
    public const int DwmwaExtendedFrameBounds = 9;

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    public delegate void TimerProc(IntPtr hwnd, uint msg, UIntPtr id, uint time);

    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Pt;
        public uint Private;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint elapseMs, TimerProc proc);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessage(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);

    public static Rect FrameBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var r, Marshal.SizeOf<Rect>()) == 0)
            return r;
        GetWindowRect(hwnd, out r);
        return r;
    }

    public static MonitorInfo MonitorAt(Point p)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromPoint(p, MonitorDefaultToNearest), ref info);
        return info;
    }

    public static bool SystemFlag(uint action) => SystemParametersInfo(action, 0, out var v, 0) && v != 0;
}
