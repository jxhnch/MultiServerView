using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MultiServerView.Core.Net;

/// <summary>Wakes a sleeping machine of the local network with the "magic packet".</summary>
public static class WakeOnLan
{
    /// <summary>Accepts AA:BB:CC:DD:EE:FF, AA-BB-CC-DD-EE-FF, aabb.ccdd.eeff and AABBCCDDEEFF.</summary>
    public static bool TryParseMac(string? text, out byte[] mac)
    {
        mac = Array.Empty<byte>();
        var digits = new string((text ?? "").Trim().Where(c => c is not (':' or '-' or '.')).ToArray());
        if (digits.Length != 12 || !digits.All(Uri.IsHexDigit)) return false;
        mac = Convert.FromHexString(digits);
        return true;
    }

    /// <summary>Six times 0xFF, then the address sixteen times.</summary>
    public static byte[] MagicPacket(byte[] mac)
    {
        var packet = new byte[6 + 16 * 6];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++) mac.CopyTo(packet, 6 + i * 6);
        return packet;
    }

    /// <summary>Broadcasts the packet on every active network of this PC (it does not get through routers).</summary>
    public static async Task SendAsync(string macAddress)
    {
        if (!TryParseMac(macAddress, out var mac)) throw new ArgumentException("This is not a MAC address (example: AA:BB:CC:DD:EE:FF).");
        var packet = MagicPacket(mac);
        var sent = 0;
        foreach (var (local, broadcast) in Networks())
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
                await udp.SendAsync(packet, packet.Length, new IPEndPoint(broadcast, 9));
                await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
                sent++;
            }
            catch (SocketException) { /* this network does not allow it: try the others */ }
        }
        if (sent == 0) throw new InvalidOperationException("The wake-up packet could not be sent: no usable network was found.");
    }

    private static IEnumerable<(IPAddress Local, IPAddress Broadcast)> Networks()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var address in network.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = address.Address.GetAddressBytes();
                var mask = address.IPv4Mask.GetAddressBytes();
                if (mask.All(b => b == 0)) continue;
                yield return (address.Address, new IPAddress(ip.Select((b, i) => (byte)(b | ~mask[i])).ToArray()));
            }
        }
    }
}
