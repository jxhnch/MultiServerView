using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiServerView.Core;

public static class Json
{
    /// <summary>camelCase, enums as lowercase strings ("ssh", "rdp", "vnc") - matches ui/src/types.ts.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false
    };
}
