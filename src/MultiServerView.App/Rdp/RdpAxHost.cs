using System.Runtime.InteropServices;

namespace MultiServerView.App.Rdp;

/// <summary>
/// WinForms wrapper around the RDP ActiveX (MsRdpClient9NotSafeForScripting, from Windows 8.1 / Server 2012 R2).
/// The CLSID comes from mstscax.dll; the control is then addressed via dynamic.
/// </summary>
internal sealed class RdpAxHost : AxHost
{
    public RdpAxHost() : base("7cacbd7b-0d99-468f-ac33-22e495c0afe5")
    {
    }
}

/// <summary>
/// The password can only be set on the RDP control through this (non-scriptable) interface.
/// Important: only the first entries of the vtable are declared; <c>ClearTextPassword</c> is the first.
/// </summary>
[ComImport]
[Guid("c1e6743a-41c1-4a74-832a-0dd06c1c7a0e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsTscNonScriptable
{
    [DispId(1)]
    string ClearTextPassword
    {
        [param: MarshalAs(UnmanagedType.BStr)]
        set;
    }
}
