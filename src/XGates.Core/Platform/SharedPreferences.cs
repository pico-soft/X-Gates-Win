using System.Text.Json;
using System.Text.Json.Nodes;

namespace XGates.Core.Platform;

/// <summary>
/// Аналог <c>SharedPreferences</c> (ключ→значение, по одному файлу на имя): <c>filesDir/shared_prefs/&lt;name&gt;.json</c>.
/// Kotlin-сторы мелких флагов (LastServerStore, BootPrefs) используют prefs, а не JSON-файлы — сохраняем это.
/// <c>apply()</c> пишет сразу (атомарно temp→rename); для мелких флагов этого достаточно.
/// </summary>
public sealed class SharedPreferences
{
    private static readonly Dictionary<string, SharedPreferences> Cache = new();
    private readonly object gate = new();
    private readonly string path;
    private JsonObject data;

    private SharedPreferences(string path)
    {
        this.path = path;
        try { data = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject() : new JsonObject(); }
        catch (Exception) { data = new JsonObject(); }
    }

    public static SharedPreferences get(Context context, string name)
    {
        var p = Path.Combine(context.filesDir, "shared_prefs", name + ".json");
        lock (Cache)
        {
            if (!Cache.TryGetValue(p, out var sp)) Cache[p] = sp = new SharedPreferences(p);
            return sp;
        }
    }

    public string? getString(string key, string? def)
    {
        lock (gate) return data[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : def;
    }

    public long getLong(string key, long def)
    {
        lock (gate) return data[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : def;
    }

    public bool getBoolean(string key, bool def)
    {
        lock (gate) return data[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;
    }

    public Editor edit() => new(this);

    public sealed class Editor(SharedPreferences sp)
    {
        private readonly List<(string k, JsonNode? v, bool remove)> ops = [];
        public Editor putString(string k, string? v) { ops.Add((k, v == null ? null : JsonValue.Create(v), v == null)); return this; }
        public Editor putLong(string k, long v) { ops.Add((k, JsonValue.Create(v), false)); return this; }
        public Editor putBoolean(string k, bool v) { ops.Add((k, JsonValue.Create(v), false)); return this; }
        public Editor remove(string k) { ops.Add((k, null, true)); return this; }

        public void apply()
        {
            lock (sp.gate)
            {
                foreach (var (k, v, rm) in ops)
                {
                    if (rm) sp.data.Remove(k);
                    else sp.data[k] = v;
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(sp.path)!);
                    KJson.atomicWrite(sp.path, sp.path + ".tmp", sp.data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception e) { Log.w("SharedPreferences", "save failed", e); }
            }
        }
    }
}
