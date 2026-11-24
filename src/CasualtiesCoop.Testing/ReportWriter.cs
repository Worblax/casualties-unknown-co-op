using System.Diagnostics;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CasualtiesCoop.Testing;

internal sealed class ReportWriter : IDisposable
{
    private const int TraceLimit = 20000;
    internal readonly string RunId;
    internal readonly string DirectoryPath;
    internal readonly string ScenarioPath;
    internal readonly bool Visible;
    internal readonly bool HoldOnFailure;
    internal readonly int HoldSeconds;
    internal readonly bool CatalogOnly;
    internal readonly string[] ProbeItems;
    internal readonly string[] ProbeMobs;
    internal readonly int? Seed;
    private readonly JObject build;
    private readonly StreamWriter trace;
    private int traced;
    private bool disposed;

    internal ReportWriter(string manifest)
    {
        JObject config = ScenarioDocument.Read(manifest, 16384);
        RunId = ScenarioDocument.String(config, "runId");
        if (!Guid.TryParseExact(RunId, "N", out _)) throw new FormatException("Invalid run ID.");
        DirectoryPath = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
        if (Path.GetFileName(DirectoryPath) != RunId) throw new FormatException("Run ID does not match the report path.");
        ScenarioPath = Path.Combine(DirectoryPath, "scenario.json");
        Visible = config.Value<bool>("visible");
        HoldOnFailure = config.Value<bool>("holdOnFailure");
        HoldSeconds = config.Value<int?>("holdSeconds") ?? 15;
        CatalogOnly = config.Value<bool>("catalogOnly");
        ProbeItems = ProbeList(config, "probeItem");
        ProbeMobs = ProbeList(config, "probeMob");
        if (ProbeItems.Length + ProbeMobs.Length > 24) throw new FormatException("Catalog probe budget exceeded.");
        Seed = config.Value<int?>("seed");
        build = ScenarioDocument.Object(config, "buildHashes");
        trace = new StreamWriter(Path.Combine(DirectoryPath, "network.jsonl"), append: false);
        Write("started.json", new { runId = RunId, pid = Process.GetCurrentProcess().Id });
    }

    private static string[] ProbeList(JObject config, string field) =>
        config[field] is JArray values ? values.Select(token => token.Type == JTokenType.String &&
            token.Value<string>() is { Length: > 0 and <= 96 } path ? path : throw new FormatException("Invalid catalog probe path."))
            .Distinct(StringComparer.Ordinal).ToArray() : Array.Empty<string>();

    internal void Trace(NetworkTrace record, int tick)
    {
        if (traced++ >= TraceLimit) return;
        var frame = record.Frame;
        ReadOnlySpan<byte> data = frame.Data.Span;
        ulong? operation = null;
        uint? revision = null;
        ulong? snapshotId = null;
        var kind = data.Length > 0 ? (MessageKind)data[0] : MessageKind.None;
        Guid session = data.Length >= 17 ? new Guid(data.Slice(1, 16)) : Guid.Empty;
        if (kind == MessageKind.ItemRequest && ItemWire.TryReadRequest(data, session, out var request))
        {
            operation = request.Operation;
            revision = request.Revision;
        }
        else if (kind == MessageKind.ItemResult && ItemWire.TryReadResult(data, session, out ulong result, out _))
        {
            operation = result;
        }
        else if (kind == MessageKind.ItemSyncRequest && ItemSnapshotWire.TryReadRequest(data, session, out ulong sync))
        {
            snapshotId = sync;
        }
        else if (kind == MessageKind.ItemSyncDone && ItemSnapshotWire.TryReadDone(data, session, out ulong done, out _, out _))
        {
            snapshotId = done;
        }
        else if (kind == MessageKind.ItemSnapshotEntry &&
            ItemSnapshotWire.TryReadState(data, kind, session, out ulong entry, out var state))
        {
            snapshotId = entry;
            revision = state.Revision;
        }
        else if (kind == MessageKind.TerrainHit &&
            SandboxGameplayWire.TryReadTerrainHit(data, session, out var tile))
        {
            revision = tile.Sequence;
        }
        uint? chunkSequence = kind == MessageKind.WorldChunkSnapshot && data.Length >= 53
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(33)) : null;
        int? chunkX = chunkSequence.HasValue ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(37)) : null;
        int? chunkY = chunkSequence.HasValue ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(39)) : null;
        trace.WriteLine(JsonConvert.SerializeObject(new
        {
            tick, time = record.Time, @event = record.Event, sender = frame.Sender, receiver = frame.Receiver, kind = kind.ToString(),
            bytes = data.Length, frame.Reliable, sessionId = session, operation, revision, snapshotId, chunkSequence, chunkX, chunkY
        }));
    }

    internal void Write(string name, object value)
    {
        string path = Path.Combine(DirectoryPath, name);
        string temporary = path + ".tmp";
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(temporary, JsonConvert.SerializeObject(value, Formatting.Indented));
        File.Move(temporary, path);
    }

    internal void Finish(ScenarioDocument? scenario, string status, string? failure, long wallTime, float simulation,
        int steps, List<object> assertions, string[] covered)
    {
        Dispose();
        Write("result.json", new
        {
            runId = RunId, scenario = scenario?.Name, level = scenario?.Level, seed = scenario?.Seed ?? Seed, status,
            durationMs = wallTime, simulationMs = simulation * 1000, view = Visible ? "visible" : "auto",
            buildHashes = build, executedSteps = steps,
            assertions, failure, coveredPaths = covered, traceTruncated = traced > TraceLimit,
            limitations = new[] { "One Unity scene. Virtual transports. Steam peer-to-peer was not exercised.",
                "Guests have independent Core replica stores with fixture positions or explicit native Body bindings.",
                "UNITY_GUEST projects onto one selected Body in the shared fixture scene." }
        });
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            trace.Dispose();
        }
    }
}
