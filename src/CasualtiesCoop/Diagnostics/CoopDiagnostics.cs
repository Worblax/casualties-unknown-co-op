using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using CasualtiesCoop.Networking;
using UnityEngine;

namespace CasualtiesCoop.Diagnostics;

internal static class CoopDiagnostics
{
    private const float SummaryInterval = 5f;
    private const int MaxCounters = 128;
    private static readonly Dictionary<(string Area, string Event), int> counters = new();
    private static readonly Dictionary<MessageKind, Traffic> traffic = new();
    private static ConfigEntry<bool>? detailed;
    private static ManualLogSource? log;
    private static float nextSummary;

    private struct Traffic
    {
        internal int Sent;
        internal int Received;
        internal int Failed;
        internal int Bytes;
    }

    internal static bool Enabled => detailed?.Value == true;

    internal static void Install(ConfigFile configuration, ManualLogSource logger)
    {
        log = logger;
        detailed = configuration.Bind("Diagnostics", "DetailedLogging", false,
            "Record co-op events and five-second summaries of network and gameplay activity on this computer.");
        nextSummary = Time.unscaledTime + SummaryInterval;
        detailed.SettingChanged += OnToggle;
    }

    internal static void SetEnabled(bool value)
    {
        if (detailed is not null)
        {
            detailed.Value = value;
        }
    }

    private static void OnToggle(object sender, System.EventArgs args)
    {
        counters.Clear();
        traffic.Clear();
        nextSummary = Time.unscaledTime + SummaryInterval;
        log?.LogInfo(Enabled ? "Detailed co-op logging enabled." : "Detailed co-op logging disabled.");
    }

    internal static void Trace(string area, string message)
    {
        if (Enabled)
        {
            log?.LogInfo($"[{area}] {message}");
        }
    }

    internal static void Count(string area, string name)
    {
        if (Enabled)
        {
            var key = (area, name);
            if (counters.TryGetValue(key, out int count) || counters.Count < MaxCounters)
            {
                counters[key] = count + 1;
            }
        }
    }

    internal static void RecordFrame(MessageKind kind, int bytes, bool sent, bool accepted = true)
    {
        if (!Enabled || kind is <= MessageKind.None or > MessageKind.SandboxAlcohol)
        {
            return;
        }

        traffic.TryGetValue(kind, out Traffic count);
        if (!accepted) count.Failed++;
        else if (sent) count.Sent++;
        else count.Received++;
        count.Bytes += accepted ? bytes : 0;
        traffic[kind] = count;
    }

    internal static void Tick()
    {
        if (!Enabled || Time.unscaledTime < nextSummary)
        {
            return;
        }
        nextSummary = Time.unscaledTime + SummaryInterval;

        if (traffic.Count > 0)
        {
            StringBuilder summary = new("[Network] Last 5s (sent/received/failed, bytes)");
            foreach (var entry in traffic)
            {
                Traffic tally = entry.Value;
                summary.Append($" | {entry.Key} {tally.Sent}/{tally.Received}/{tally.Failed}, {tally.Bytes}");
            }
            log?.LogInfo(summary.ToString());
            traffic.Clear();
        }

        foreach (var entry in counters)
        {
            log?.LogInfo($"[{entry.Key.Area}] Last 5s {entry.Key.Event} = {entry.Value}");
        }
        counters.Clear();
    }

    internal static void Close()
    {
        if (detailed is not null)
        {
            detailed.SettingChanged -= OnToggle;
        }
        detailed = null;
        log = null;
        counters.Clear();
        traffic.Clear();
    }
}
