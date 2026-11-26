using System.Net;
using System.Net.Sockets;
using MultiServerView.Core;
using MultiServerView.Core.Net;
using Xunit;

namespace MultiServerView.Core.Tests;

public class NetTests
{
    [Theory]
    [InlineData("AA:BB:CC:00:11:22")]
    [InlineData("aa-bb-cc-00-11-22")]
    [InlineData("aabb.cc00.1122")]
    [InlineData(" AABBCC001122 ")]
    public void Mac_addresses_are_parsed(string text)
    {
        Assert.True(WakeOnLan.TryParseMac(text, out var mac));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0x00, 0x11, 0x22 }, mac);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AA:BB:CC:00:11")]
    [InlineData("GG:BB:CC:00:11:22")]
    public void Bad_mac_addresses_are_refused(string text) => Assert.False(WakeOnLan.TryParseMac(text, out _));

    [Fact]
    public void Magic_packet_has_the_known_form()
    {
        var packet = WakeOnLan.MagicPacket(new byte[] { 1, 2, 3, 4, 5, 6 });
        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, packet.Skip(96));
    }

    [Fact]
    public async Task An_open_port_is_up_and_a_closed_one_is_not()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var up = new ServerEntry { Name = "a", Host = "127.0.0.1", Port = port };
        Assert.Equal(Reachability.Up, (await Reachability.CheckAsync(new[] { up }, TimeSpan.FromSeconds(3)))[up.Id]);
        listener.Stop();
        Assert.NotEqual(Reachability.Up, await Reachability.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Some_hosts_cannot_be_checked()
    {
        Assert.Null(Reachability.TargetOf(new ServerEntry { Protocol = Protocol.Serial, Host = "COM3", Port = 9600 }));
        Assert.Null(Reachability.TargetOf(new ServerEntry { Host = "a", JumpHostId = "x" }));
        Assert.Equal(("fw.lan", 8443), Reachability.TargetOf(new ServerEntry { Protocol = Protocol.Web, Host = "https://fw.lan:8443/x" }));
        Assert.Equal(("a", 22), Reachability.TargetOf(new ServerEntry { Host = "a", Port = 0 }));
    }
}
