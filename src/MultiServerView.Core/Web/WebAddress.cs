namespace MultiServerView.Core.Web;

/// <summary>What somebody types into the address bar of the integrated browser.</summary>
public static class WebAddress
{
    /// <summary>Only http and https pages can be opened (no file:, javascript:, ...). Without a scheme, https is used.</summary>
    public static bool TryNormalize(string? typed, out string address)
    {
        address = "";
        var text = UrlResolver.Repair(typed);
        if (text.Length == 0 || text.Any(char.IsWhiteSpace)) return false;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return false;
        address = uri.AbsoluteUri;
        return true;
    }
}
