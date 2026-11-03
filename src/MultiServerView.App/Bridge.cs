using System.Text.Json;
using MultiServerView.Core;

namespace MultiServerView.App;

/// <summary>
/// Nimmt Nachrichten vom UI entgegen ({ id, method, params }) und antwortet mit { id, result } oder { id, error }.
/// Die Methoden-Namen sind identisch mit ui/src/bridge.ts.
/// </summary>
internal sealed class Bridge
{
    private readonly VaultStore _vault;

    public Bridge(VaultStore vault) => _vault = vault;

    public string Handle(string requestJson)
    {
        int id = 0;
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt32();
            var method = root.GetProperty("method").GetString() ?? "";
            var p = root.TryGetProperty("params", out var pp) ? pp : default;

            var result = Dispatch(method, p);
            return JsonSerializer.Serialize(new { id, result }, Json.Options);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { id, error = Friendly(ex) }, Json.Options);
        }
    }

    private object? Dispatch(string method, JsonElement p)
    {
        switch (method)
        {
            case "vault.status":
                return new { exists = _vault.Exists, unlocked = _vault.IsUnlocked };
            case "vault.create":
                _vault.Create(Str(p, "password"));
                return null;
            case "vault.unlock":
                _vault.Unlock(Str(p, "password"));
                return null;
            case "vault.lock":
                _vault.Lock();
                return null;
            case "servers.list":
                return _vault.List();
            case "servers.save":
                return _vault.Upsert(p.Deserialize<ServerEntry>(Json.Options)
                                     ?? throw new ArgumentException("Ungültiger Server."));
            case "servers.delete":
                _vault.Delete(Str(p, "id"));
                return null;
            case "servers.touch":
                return _vault.Touch(Str(p, "id"));
            default:
                throw new InvalidOperationException($"Unbekannte Methode: {method}");
        }
    }

    private static string Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : throw new ArgumentException($"Parameter fehlt: {name}");

    private static string Friendly(Exception ex) => ex switch
    {
        WrongPasswordException => "Falsches Passwort.",
        ArgumentException or InvalidOperationException or KeyNotFoundException => ex.Message,
        System.Security.Cryptography.CryptographicException => "Die Vault-Datei ist ungültig oder beschädigt.",
        _ => "Unerwarteter Fehler: " + ex.Message
    };
}
