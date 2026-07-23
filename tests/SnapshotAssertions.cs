using System;
using System.Collections.Generic;
using System.Linq;
using Lurp.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lurp.Storage.Tests;

/// <summary>
/// Shared snapshot-comparison helpers used by both CleanRebuildEquivalenceTest
/// and RealSolutionIntegrationTests. Extracted to avoid duplication across the
/// two equivalence-test classes.
/// </summary>
internal static class SnapshotAssertions
{
    public static void CompareSnapshotsAreEquivalent(
        string dbPath, string snapshotB, string snapshotC)
    {
        Assert.NotEqual(snapshotB, snapshotC);

        var store = new SqliteIndexStore(dbPath);
        store.Open(dbPath);

        try
        {
            var symbolsB = store.GetSymbolIdsInSnapshot(snapshotB);
            var symbolsC = store.GetSymbolIdsInSnapshot(snapshotC);
            symbolsB.Sort(StringComparer.Ordinal);
            symbolsC.Sort(StringComparer.Ordinal);

            Assert.Equal(symbolsC.Count, symbolsB.Count);
            Assert.True(
                symbolsB.SequenceEqual(symbolsC, StringComparer.Ordinal),
                $"Symbol set mismatch between B ({snapshotB}) and C ({snapshotC}).\n" +
                $"  B count: {symbolsB.Count}, C count: {symbolsC.Count}\n" +
                $"  Only in B: {string.Join(", ", symbolsB.Except(symbolsC, StringComparer.Ordinal).Take(10))}\n" +
                $"  Only in C: {string.Join(", ", symbolsC.Except(symbolsB, StringComparer.Ordinal).Take(10))}");

            var edgesB = store.GetEdges(snapshotB);
            var edgesC = store.GetEdges(snapshotC);
            NormalizeEdges(edgesB);
            NormalizeEdges(edgesC);

            if (edgesC.Count != edgesB.Count)
            {
                var bSet = edgesB.Select(e => $"{e.SourceSymbolId}|{e.TargetSymbolId}|{e.Kind}|{e.Provenance}").ToHashSet();
                var cSet = edgesC.Select(e => $"{e.SourceSymbolId}|{e.TargetSymbolId}|{e.Kind}|{e.Provenance}").ToHashSet();
                Assert.Fail($"Edge count mismatch: {edgesB.Count} (B:incremental) vs {edgesC.Count} (C:full rebuild).\n" +
                    $"Only in B: {string.Join(", ", bSet.Except(cSet).Take(10))}\n" +
                    $"Only in C: {string.Join(", ", cSet.Except(bSet).Take(10))}");
            }

            Assert.Equal(edgesC.Count, edgesB.Count);

            var diagB = store.GetDiagnostics(snapshotB);
            var diagC = store.GetDiagnostics(snapshotC);
            NormalizeDiagnostics(diagB);
            NormalizeDiagnostics(diagC);

            Assert.Equal(diagC.Count, diagB.Count);
            for (int i = 0; i < diagC.Count && i < diagB.Count; i++)
            {
                AssertEqual(diagB[i], diagC[i]);
            }

            var annB = store.GetAnnotations(snapshotB);
            var annC = store.GetAnnotations(snapshotC);
            NormalizeAnnotations(annB);
            NormalizeAnnotations(annC);

            Assert.Equal(annC.Count, annB.Count);
            for (int i = 0; i < annC.Count && i < annB.Count; i++)
            {
                AssertEqual(annB[i], annC[i]);
            }

            var ftsCountsB = GetFtsCounts(dbPath, snapshotB);
            var ftsCountsC = GetFtsCounts(dbPath, snapshotC);
            Assert.Equal(ftsCountsC.SourceRows, ftsCountsB.SourceRows);
            Assert.Equal(ftsCountsC.SymbolRows, ftsCountsB.SymbolRows);
        }
        finally
        {
            store.Close();
        }
    }

    public static void NormalizeEdges(List<EdgeRecord> edges)
    {
        foreach (var edge in edges)
        {
            var field = typeof(EdgeRecord).GetField("<SnapshotId>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field != null)
                field.SetValue(edge, string.Empty);
        }

        edges.Sort((a, b) =>
        {
            int cmp = StringComparer.Ordinal.Compare(a.SourceSymbolId, b.SourceSymbolId);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.TargetSymbolId, b.TargetSymbolId);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.Kind, b.Kind);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.Provenance, b.Provenance);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.SourceDocumentPath ?? "", b.SourceDocumentPath ?? "");
            if (cmp != 0) return cmp;
            return (a.SourceStartLine ?? 0).CompareTo(b.SourceStartLine ?? 0);
        });
    }

    public static void NormalizeDiagnostics(List<DiagnosticRecord> diags)
    {
        diags.Sort((a, b) =>
        {
            int cmp = StringComparer.Ordinal.Compare(a.DocumentPath ?? "", b.DocumentPath ?? "");
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.Id, b.Id);
            if (cmp != 0) return cmp;
            return (a.StartLine ?? 0).CompareTo(b.StartLine ?? 0);
        });
    }

    public static void NormalizeAnnotations(List<AnnotationRecord> annotations)
    {
        annotations.Sort((a, b) =>
        {
            int cmp = StringComparer.Ordinal.Compare(a.SymbolId, b.SymbolId);
            if (cmp != 0) return cmp;
            return StringComparer.Ordinal.Compare(a.Kind, b.Kind);
        });
    }

    public static void AssertEqual(EdgeRecord expected, EdgeRecord actual)
    {
        Assert.Equal(expected.SourceSymbolId, actual.SourceSymbolId);
        Assert.Equal(expected.TargetSymbolId, actual.TargetSymbolId);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Provenance, actual.Provenance);
        Assert.Equal(expected.SnapshotId ?? "", actual.SnapshotId ?? "");
        Assert.Equal(expected.ExtractorVersion ?? "", actual.ExtractorVersion ?? "");
        Assert.Equal(expected.SourceDocumentPath ?? "", actual.SourceDocumentPath ?? "");
        Assert.Equal(expected.SourceStartLine, actual.SourceStartLine);
        Assert.Equal(expected.SourceEndLine, actual.SourceEndLine);
        Assert.Equal(expected.SourceStartColumn, actual.SourceStartColumn);
        Assert.Equal(expected.SourceEndColumn, actual.SourceEndColumn);
    }

    public static void AssertEqual(DiagnosticRecord expected, DiagnosticRecord actual)
    {
        Assert.Equal(expected.ProjectName, actual.ProjectName);
        Assert.Equal(expected.DocumentPath, actual.DocumentPath);
        Assert.Equal(expected.Severity, actual.Severity);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(expected.StartLine, actual.StartLine);
        Assert.Equal(expected.StartColumn, actual.StartColumn);
        Assert.Equal(expected.EndLine, actual.EndLine);
        Assert.Equal(expected.EndColumn, actual.EndColumn);
    }

    public static void AssertEqual(AnnotationRecord expected, AnnotationRecord actual)
    {
        Assert.Equal(expected.SymbolId, actual.SymbolId);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Value, actual.Value);
    }

    /// <summary>
    /// Return (source_fts rows, symbol_fts rows) for a snapshot.
    /// Opens a fresh connection so the read is independent of any store lifecycle.
    /// </summary>
    public static (int SourceRows, int SymbolRows) GetFtsCounts(string dbPath, string snapshotId)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM source_fts WHERE snapshot_id = @id;";
        cmd.Parameters.AddWithValue("@id", snapshotId);
        var sourceRows = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);

        cmd.CommandText = "SELECT COUNT(*) FROM symbol_fts WHERE snapshot_id = @id;";
        var symbolRows = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);

        return (sourceRows, symbolRows);
    }

    public static void SqliteConnectionClearAllPools()
    {
        try
        {
            SqliteConnection.ClearAllPools();
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
