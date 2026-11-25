namespace MultiServerView.Core;

/// <summary>A login that is given to several hosts at once: an identity, an SSH key, or user name and password.</summary>
public sealed record BulkLogin(string Mode, string IdentityId = "", string KeyId = "", string Username = "", string Password = "")
{
    public const string ByIdentity = "identity";
    public const string ByKey = "key";
    public const string ByPassword = "password";
}
