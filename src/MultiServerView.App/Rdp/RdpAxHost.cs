using System.Runtime.InteropServices;

namespace MultiServerView.App.Rdp;

/// <summary>
/// WinForms-Hülle um das RDP-ActiveX (MsRdpClient9NotSafeForScripting, ab Windows 8.1 / Server 2012 R2).
/// Die CLSID kommt aus mstscax.dll; das Control wird danach per dynamic angesprochen.
/// </summary>
internal sealed class RdpAxHost : AxHost
{
    public RdpAxHost() : base("7cacbd7b-0d99-468f-ac33-22e495c0afe5")
    {
    }
}

/// <summary>
/// Das Passwort lässt sich beim RDP-Control nur über dieses (nicht-skriptbare) Interface setzen.
/// Wichtig: Es sind nur die ersten Einträge der vtable deklariert; <c>ClearTextPassword</c> ist der erste.
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
