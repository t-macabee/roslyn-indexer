using Lurp.Storage;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lurp.Workspace;

public sealed class AuditOptions
{
    public HashSet<string> Checks { get; }
    public int FanOutThreshold { get; }

    public AuditOptions(HashSet<string> checks, int fanOutThreshold = 20)
    {
        if (checks.Contains("all", StringComparer.OrdinalIgnoreCase))
        {
            Checks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "dead-symbol", "untested-surface", "unregistered-impl", "high-fan-out"
            };
        }
        else
        {
            Checks = checks ?? throw new ArgumentNullException(nameof(checks));
        }
        FanOutThreshold = fanOutThreshold;
    }
}

public sealed class AuditFinding(string check, string symbolId, string? fqn = null, string? detail = null)
{
    [JsonPropertyName("check")]
    public string Check { get; } = check ?? throw new ArgumentNullException(nameof(check));

    [JsonPropertyName("symbol_id")]
    public string SymbolId { get; } = symbolId ?? throw new ArgumentNullException(nameof(symbolId));

    [JsonPropertyName("fqn")]
    public string? Fqn { get; } = fqn;

    [JsonPropertyName("detail")]
    public string? Detail { get; } = detail;
}

public sealed class AuditReport(string snapshotId, List<string> checksRun, List<AuditFinding> findings)
{
    [JsonPropertyName("snapshot_id")]
    public string SnapshotId { get; } = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));

    [JsonPropertyName("checks_run")]
    public List<string> ChecksRun { get; } = checksRun ?? throw new ArgumentNullException(nameof(checksRun));

    [JsonPropertyName("findings")]
    public List<AuditFinding> Findings { get; } = findings ?? throw new ArgumentNullException(nameof(findings));
}

public sealed class AuditEngine(IIndexStore store, string snapshotId)
{
    private readonly IIndexStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly string _snapshotId = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));

    public AuditReport RunAudit(AuditOptions options)
    {
        var findings = new List<AuditFinding>();
        var checksRun = options.Checks.ToList();

        var allSymbolIds = _store.GetSymbolIdsInSnapshot(_snapshotId);
        var symbolInfoCache = new Dictionary<string, IndexedSymbolInfo?>();

        IndexedSymbolInfo? GetInfo(string symbolId)
        {
            if (!symbolInfoCache.TryGetValue(symbolId, out var info))
            {
                info = _store.GetSymbolInfo(symbolId, _snapshotId);

                symbolInfoCache[symbolId] = info;
            }
            return info;
        }

        if (options.Checks.Contains("dead-symbol", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var symbolId in allSymbolIds)
            {
                var incoming = _store.GetIncomingEdges(_snapshotId, symbolId);
                var relevant = incoming.Where(e => e.Kind is "Calls" or "References" or "Overrides" or "Implements");

                if (!relevant.Any())
                {
                    var info = GetInfo(symbolId);
                    findings.Add(new AuditFinding(check: "dead-symbol", symbolId: symbolId, fqn: info?.FullyQualifiedName));
                }
            }
        }

        if (options.Checks.Contains("untested-surface", StringComparer.OrdinalIgnoreCase))
        {
            var covered = _store.GetEdgesByKind(_snapshotId, "TestedBy")
                .Select(e => e.TargetSymbolId)
                .ToHashSet();

            foreach (var symbolId in allSymbolIds)
            {
                if (covered.Contains(symbolId))
                    continue;

                var info = GetInfo(symbolId);

                if (info?.MetadataJson == null)
                    continue;

                try
                {
                    var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(info.MetadataJson);

                    if (metadata != null && metadata.TryGetValue("accessibility", out var accEl))
                    {
                        var acc = accEl.GetString();

                        if (acc is "public" or "internal")
                        {
                            findings.Add(new AuditFinding(check: "untested-surface", symbolId: symbolId, fqn: info.FullyQualifiedName));
                        }
                    }
                }
                catch (JsonException)
                {

                }
            }
        }

        if (options.Checks.Contains("unregistered-impl", StringComparer.OrdinalIgnoreCase))
        {
            var registered = _store.GetEdgesByKind(_snapshotId, "Registers")
                .Select(e => e.TargetSymbolId)
                .ToHashSet();

            var implementsEdges = _store.GetEdgesByKind(_snapshotId, "Implements");

            foreach (var edge in implementsEdges)
            {
                if (!registered.Contains(edge.SourceSymbolId))
                {
                    var info = GetInfo(edge.SourceSymbolId);

                    findings.Add(new AuditFinding(check: "unregistered-impl", symbolId: edge.SourceSymbolId, fqn: info?.FullyQualifiedName, detail: $"implements {edge.TargetSymbolId}"));
                }
            }
        }

        if (options.Checks.Contains("high-fan-out", StringComparer.OrdinalIgnoreCase))
        {
            var fanOutFindings = new List<(AuditFinding finding, int count)>();

            foreach (var symbolId in allSymbolIds)
            {
                var outgoing = _store.GetOutgoingEdges(_snapshotId, symbolId);
                var callCount = outgoing.Count(e => e.Kind == "Calls");

                if (callCount > options.FanOutThreshold)
                {
                    var info = GetInfo(symbolId);
                    fanOutFindings.Add((new AuditFinding(check: "high-fan-out", symbolId: symbolId, fqn: info?.FullyQualifiedName, detail: callCount.ToString()),
                        callCount));
                }
            }

            fanOutFindings.Sort((a, b) => b.count.CompareTo(a.count));
            findings.AddRange(fanOutFindings.Select(f => f.finding));
        }

        return new AuditReport(_snapshotId, checksRun, findings);
    }
}
