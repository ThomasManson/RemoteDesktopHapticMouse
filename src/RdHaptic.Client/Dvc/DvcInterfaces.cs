using System.Runtime.InteropServices;

namespace RdHaptic.Client.Dvc;

// Client-side dynamic virtual channel interfaces from tsvirtualchannels.idl (Windows SDK).
// Method order matters: these are vtable layouts.

[ComImport, Guid("A1230201-1439-4e62-a414-190d0ac3d40e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSPlugin
{
    [PreserveSig] int Initialize(IWTSVirtualChannelManager channelManager);
    [PreserveSig] int Connected();
    [PreserveSig] int Disconnected(uint disconnectCode);
    [PreserveSig] int Terminated();
}

[ComImport, Guid("A1230205-d6a7-11d8-b9fd-000bdbd1f198"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSVirtualChannelManager
{
    [PreserveSig]
    int CreateListener(
        [MarshalAs(UnmanagedType.LPStr)] string channelName,
        uint flags,
        IWTSListenerCallback listenerCallback,
        out IWTSListener? listener);
}

[ComImport, Guid("A1230206-9a39-4d58-8674-cdb4dff4e73b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSListener
{
    [PreserveSig] int GetConfiguration(out IntPtr propertyBag);
}

[ComImport, Guid("A1230203-d6a7-11d8-b9fd-000bdbd1f198"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSListenerCallback
{
    [PreserveSig]
    int OnNewChannelConnection(
        IWTSVirtualChannel channel,
        [MarshalAs(UnmanagedType.BStr)] string? data,
        out int accept,
        out IWTSVirtualChannelCallback? callback);
}

[ComImport, Guid("A1230204-d6a7-11d8-b9fd-000bdbd1f198"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSVirtualChannelCallback
{
    [PreserveSig] int OnDataReceived(uint size, IntPtr buffer);
    [PreserveSig] int OnClose();
}

[ComImport, Guid("A1230207-d6a7-11d8-b9fd-000bdbd1f198"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSVirtualChannel
{
    [PreserveSig] int Write(uint size, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] byte[] buffer, IntPtr reserved);
    [PreserveSig] int Close();
}
