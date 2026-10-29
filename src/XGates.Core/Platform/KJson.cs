using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XGates.Core.Platform;

/// <summary>
/// Аналог настроек kotlinx.serialization в сторах (<c>Json { prettyPrint; ignoreUnknownKeys; encodeDefaults }</c>):
/// незнакомые поля игнорируются (System.Text.Json — по умолчанию), отсутствующие берут дефолт инициализатора,
/// все поля (и null) пишутся, кириллица — как есть. Имена полей — из [JsonPropertyName] (как в Kotlin).
/// </summary>
public static class KJson
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly JsonSerializerOptions Compact = new(Pretty) { WriteIndented = false };

    public static string encodeToString<T>(T value, JsonSerializerOptions? o = null) => JsonSerializer.Serialize(value, o ?? Pretty);

    public static T decodeFromString<T>(string json, JsonSerializerOptions? o = null) =>
        JsonSerializer.Deserialize<T>(json, o ?? Pretty) ?? throw new JsonException("null");

    /// <summary>Атомарная запись temp→rename (как в Kotlin-сторах: tmp.writeText → renameTo → запасной copy).</summary>
    public static void atomicWrite(string target, string tmp, string text)
    {
        File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
        File.Move(tmp, target, overwrite: true);
    }
}
