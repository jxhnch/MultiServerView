using System.Text.Json;
using MultiServerView.Core;
using Xunit;

namespace MultiServerView.Core.Tests;

public class TeamviewerTests
{
    [Fact]
    public void Protocol_is_stored_as_teamviewer_and_has_no_port()
    {
        var json = JsonSerializer.Serialize(new ServerEntry { Name = "support", Host = "123456789", Protocol = Protocol.Teamviewer }, Json.Options);
        Assert.Contains("\"protocol\":\"teamviewer\"", json);
        Assert.Equal(Protocol.Teamviewer, JsonSerializer.Deserialize<ServerEntry>(json, Json.Options)!.Protocol);
        Assert.Equal(0, ServerEntry.DefaultPort(Protocol.Teamviewer));
    }
}
