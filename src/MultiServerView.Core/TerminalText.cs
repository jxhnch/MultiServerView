using System.Text;

namespace MultiServerView.Core;

/// <summary>
/// Makes text safe to print into a terminal. Host names, tags, workspaces and error messages can come from other people
/// (a Proxmox VM name, an imported file, the message of a server). If such text contains escape sequences the terminal would
/// obey them (clear the screen, redraw the list, write to the clipboard with OSC 52, ...). Only text that is shown to the user is
/// cleaned, never the data stream of a session.
/// </summary>
public static class TerminalText
{
    /// <summary>Replaces control characters (including ESC), DEL, C1 controls and text direction overrides with "?".</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            int n = c;
            var bad = n < 0x20 || n == 0x7f || (n >= 0x80 && n <= 0x9f)
                      || (n >= 0x202a && n <= 0x202e) || (n >= 0x2066 && n <= 0x2069)
                      || n == 0x200e || n == 0x200f || n == 0x2028 || n == 0x2029;
            sb.Append(bad ? '?' : c);
        }
        return sb.ToString();
    }
}
