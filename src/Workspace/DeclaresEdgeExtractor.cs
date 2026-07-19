using Lurp.Storage;
using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class DeclaresEdgeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var typeSymbol in context.GetAllNamedTypes())
        {
            var typeId = context.MakeSymbolId(typeSymbol);

            if (typeId == null)
                continue;

            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is INamedTypeSymbol)
                    continue;

                var memberId = context.MakeSymbolId(member);

                if (memberId == null)
                    continue;

                var key = (typeId, memberId, EdgeKind.Declares.ToString());

                if (!seen.Add(key))
                    continue;

                var loc = context.GetMemberSourceLocation(member);

                edges.Add(new EdgeRecord
                {
                    SourceSymbolId = typeId,
                    TargetSymbolId = memberId,
                    Kind = EdgeKind.Declares.ToString(),
                    Provenance = "compiler_proved",
                    SnapshotId = context.SnapshotId,
                    ExtractorVersion = ExtractorConstants.DeclaresExtractor,
                    SourceDocumentPath = loc?.path,
                    SourceStartLine = loc?.startLine,
                    SourceStartColumn = loc?.startColumn,
                    SourceEndLine = loc?.endLine,
                    SourceEndColumn = loc?.endColumn,
                });
            }
        }

        return edges;
    }
}
