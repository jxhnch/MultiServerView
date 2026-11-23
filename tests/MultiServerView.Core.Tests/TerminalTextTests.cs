using MultiServerView.Core;
using Xunit;

namespace MultiServerView.Core.Tests;

public class TerminalTextTests
{
    [Fact]
    public void Escape_sequences_are_neutralised()
    {
        var text = TerminalText.Sanitize("web\u001b]52;c;AAAA\u0007\u001b[2J\r\nrm");
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('\u0007', text);
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void C1_controls_and_direction_overrides_are_removed()
    {
        var text = TerminalText.Sanitize("a\u009bb‮c⁦d\u007f");
        Assert.Equal("a?b?c?d?", text);
    }

    [Fact]
    public void Normal_text_is_unchanged()
    {
        Assert.Equal("Server ünï – 東京 web-01", TerminalText.Sanitize("Server ünï – 東京 web-01"));
        Assert.Equal("", TerminalText.Sanitize(null));
    }
}
