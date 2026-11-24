using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CasualtiesCoop.Testing;

internal sealed class ScenarioDocument
{
    internal readonly string Name;
    internal readonly string Level;
    internal readonly int Seed;
    internal readonly int TimeoutMs;
    internal readonly int ItemLimit;
    internal readonly int MobLimit;
    internal readonly int BodyLimit;
    internal readonly bool StandaloneItems;
    internal readonly NetworkProfile Profile;
    internal readonly JObject[] Setup;
    internal readonly JObject[] Steps;

    internal ScenarioDocument(string file, int? seed)
    {
        JObject root = Read(file, 65536);
        Fields(root, "schemaVersion", "name", "level", "seed", "timeoutMs", "network", "setup", "steps", "limits", "standaloneItems");
        if (Integer(root, "schemaVersion", 1, 1) != 1) throw new FormatException("Unsupported scenario schema.");
        Name = String(root, "name");
        Level = String(root, "level");
        Seed = seed ?? Integer(root, "seed", int.MinValue, int.MaxValue);
        TimeoutMs = Integer(root, "timeoutMs", 500, 180000);
        Profile = ReadProfile(Object(root, "network"));
        JObject limits = root["limits"] is null ? new JObject() : Object(root, "limits");
        Fields(limits, "items", "mobs", "bodies");
        ItemLimit = OptionalInt(limits, "items", 1, 64, 16);
        MobLimit = OptionalInt(limits, "mobs", 1, 16, 6);
        BodyLimit = OptionalInt(limits, "bodies", 1, 8, 4);
        StandaloneItems = root["standaloneItems"] is not null && Flag(root, "standaloneItems");
        Setup = Commands(root, "setup");
        Steps = Commands(root, "steps");
        foreach (JObject command in Setup) ScenarioCommands.Validate(command, CommandPhase.Setup);
        foreach (JObject command in Steps) ScenarioCommands.Validate(command, CommandPhase.Step);
    }

    internal static NetworkProfile ReadProfile(JObject network)
    {
        Fields(network, "latencyMs", "jitterMs", "unreliableLoss", "unreliableDuplication", "unreliableReorderMs", "bytesPerSecond");
        return new NetworkProfile(Number(network, "latencyMs", 3000) / 1000, Number(network, "jitterMs", 3000) / 1000,
            Number(network, "unreliableLoss", 1), Number(network, "unreliableDuplication", 1),
            Number(network, "unreliableReorderMs", 3000) / 1000, (int)Number(network, "bytesPerSecond", 10000000));
    }

    internal static JObject Read(string path, long max)
    {
        if (new FileInfo(path).Length > max) throw new FormatException("JSON file exceeds size limit.");
        using var input = File.OpenText(path);
        using var reader = new JsonTextReader(input) { MaxDepth = 20, DateParseHandling = DateParseHandling.None };
        JObject root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new FormatException("Trailing JSON.");
        return root;
    }

    internal static void Fields(JObject value, params string[] allowed)
    {
        foreach (JProperty field in value.Properties())
            if (!allowed.Contains(field.Name)) throw new FormatException($"Unknown field {field.Name}.");
    }

    internal static JObject Object(JObject root, string key) => root[key] as JObject ?? throw new FormatException($"Missing object {key}.");

    internal static string String(JObject root, string key) =>
        root[key] is JValue { Type: JTokenType.String, Value: string value } && value.Length is > 0 and <= 96
            ? value : throw new FormatException($"Invalid text {key}.");

    internal static int Integer(JObject root, string key, int min, int max) =>
        root[key] is JValue { Type: JTokenType.Integer, Value: long value } && value >= min && value <= max
            ? (int)value : throw new FormatException($"Invalid integer {key}.");

    internal static (float X, float Y) Position(JObject root)
    {
        if (root["position"] is not JArray { Count: 2 } position) throw new FormatException("Invalid position.");
        return (Number(position[0], 64, -64), Number(position[1], 64, -64));
    }

    private static float Number(JObject root, string key, float max) => root[key] is { } value ? Number(value, max, minimum: 0) : 0;

    private static float Number(JToken value, float max, float minimum = -32)
    {
        float number = value.Type is JTokenType.Integer or JTokenType.Float ? value.Value<float>() : float.NaN;
        return float.IsFinite(number) && number >= minimum && number <= max ? number : throw new FormatException("Invalid number.");
    }

    internal static int OptionalInt(JObject root, string key, int min, int max, int fallback) =>
        root[key] is null ? fallback : Integer(root, key, min, max);

    internal static float Scalar(JObject root, string key, float min, float max) =>
        root[key] is { } value ? Number(value, max, min) : throw new FormatException($"Missing number {key}.");

    internal static bool Flag(JObject root, string key) =>
        root[key] is JValue { Type: JTokenType.Boolean } value ? value.Value<bool>() : throw new FormatException($"Missing boolean {key}.");

    internal static JObject[] Commands(JObject root, string key) =>
        root[key] is JArray { Count: > 0 and <= 48 } array && array.All(value => value is JObject)
            ? array.Cast<JObject>().ToArray() : throw new FormatException($"Invalid commands {key}.");
}

internal sealed class TestBlocked : Exception
{
    internal TestBlocked(string message) : base(message) { }
}
