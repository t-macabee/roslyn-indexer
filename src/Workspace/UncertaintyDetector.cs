using Lurp.Storage;

namespace Lurp.Workspace
{
    internal sealed class UncertaintyDetector
    {
        private readonly IEdgeStore _edgeStore;
        private readonly IDeclarationStore _declarationStore;
        private readonly string _snapshotId;
        private readonly SymbolId _symbolId;
        private readonly bool _includeGenerated;

        public UncertaintyDetector(
            IEdgeStore edgeStore,
            IDeclarationStore declarationStore,
            string snapshotId,
            SymbolId symbolId,
            bool includeGenerated)
        {
            _edgeStore = edgeStore;
            _declarationStore = declarationStore;
            _snapshotId = snapshotId;
            _symbolId = symbolId;
            _includeGenerated = includeGenerated;
        }

        public void Detect(ContextCapsule capsule)
        {
            PopulateUncertainties(capsule);
            PopulateSuggestedVerification(capsule);
        }

        private void PopulateUncertainties(ContextCapsule capsule)
        {
            var neighborhood = BuildNeighborhood(capsule);

            CollectReflectionUncertainties(capsule, neighborhood);
            CollectDispatchUncertainties(capsule, neighborhood);
            CollectFrameworkConventionUncertainties(capsule, neighborhood);

            if (!_includeGenerated)
                CollectGeneratedExclusionUncertainties(capsule, neighborhood);
        }

        private HashSet<string> BuildNeighborhood(ContextCapsule capsule)
        {
            var neighborhood = new HashSet<string> { _symbolId.Value };

            AddSymbolIds(neighborhood, capsule.Contracts);
            AddSymbolIds(neighborhood, capsule.DirectCallees);
            AddSymbolIds(neighborhood, capsule.DirectCallers);
            AddSymbolIds(neighborhood, capsule.RegisteredImplementations);
            AddSymbolIds(neighborhood, capsule.RelevantTests);
            AddSymbolIds(neighborhood, capsule.SecondDegreeContext);
            AddSymbolIds(neighborhood, capsule.SurroundingSource);

            var anchorEdges = _edgeStore.GetIncomingEdges(_snapshotId, _symbolId.Value)
                .Concat(_edgeStore.GetOutgoingEdges(_snapshotId, _symbolId.Value))
                .ToList();

            foreach (var edge in anchorEdges)
            {
                neighborhood.Add(edge.SourceSymbolId);
                neighborhood.Add(edge.TargetSymbolId);
            }

            return neighborhood;
        }

        private static void AddSymbolIds(HashSet<string> neighborhood, IEnumerable<CapsuleItem> items)
        {
            foreach (var item in items)
                neighborhood.Add(item.SymbolId);
        }

        private void CollectReflectionUncertainties(ContextCapsule capsule, HashSet<string> neighborhood)
        {
            foreach (var symbolId in neighborhood)
            {
                var edges = _edgeStore.GetIncomingEdges(_snapshotId, symbolId)
                    .Concat(_edgeStore.GetOutgoingEdges(_snapshotId, symbolId));

                foreach (var edge in edges)
                {
                    if (edge.Kind == EdgeKind.ReflectionNameCandidate.ToString())
                    {
                        capsule.Uncertainties.Add(new UncertaintyEntry([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, $"Reflection name candidate: the string-based reference to '{edge.TargetSymbolId}' was matched by name. Verify that this reference correctly resolves at runtime."));
                    }
                    else if (edge.Kind == EdgeKind.ReflectionTargetUnknown.ToString())
                    {
                        capsule.Uncertainties.Add(new UncertaintyEntry([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, "Unknown reflection target: the runtime target of this reflection call cannot be statically determined."));
                    }
                }
            }
        }

        private void CollectDispatchUncertainties(ContextCapsule capsule, HashSet<string> neighborhood)
        {
            foreach (var symbolId in neighborhood)
            {
                var outgoing = _edgeStore.GetOutgoingEdges(_snapshotId, symbolId);
                foreach (var edge in outgoing)
                {
                    if (edge.Kind != EdgeKind.MayDispatchTo.ToString())
                        continue;
                    if (edge.Provenance == Provenance.CompilerProved || edge.Provenance == Provenance.FrameworkDerived)
                        continue;

                    capsule.Uncertainties.Add(new UncertaintyEntry([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, $"Dispatch candidate '{edge.TargetSymbolId}' was resolved with evidence level '{edge.Provenance}'. Manually verify that the runtime dispatch reaches the correct implementation."));
                }
            }
        }

        private void CollectFrameworkConventionUncertainties(ContextCapsule capsule, HashSet<string> neighborhood)
        {
            var frameworkKinds = new HashSet<string>
            {
                EdgeKind.RoutesTo.ToString(),
                EdgeKind.Handles.ToString(),
                EdgeKind.Registers.ToString()
            };

            foreach (var symbolId in neighborhood)
            {
                var edges = _edgeStore.GetIncomingEdges(_snapshotId, symbolId)
                    .Concat(_edgeStore.GetOutgoingEdges(_snapshotId, symbolId));

                foreach (var edge in edges)
                {
                    if (!frameworkKinds.Contains(edge.Kind))
                        continue;
                    if (edge.Provenance != Provenance.Convention)
                        continue;

                    capsule.Uncertainties.Add(new UncertaintyEntry([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, $"Convention-based framework binding: the '{edge.Kind}' edge was inferred by naming convention, not explicit registration. Verify that the expected target is reached at runtime."));
                }
            }
        }

        private void CollectGeneratedExclusionUncertainties(ContextCapsule capsule, HashSet<string> neighborhood)
        {
            foreach (var symbolId in neighborhood)
            {
                var hasGeneratedSource = _declarationStore.GetSymbolSource(symbolId, _snapshotId, ViewKind.Declaration, true);
                var hasNonGeneratedSource = _declarationStore.GetSymbolSource(symbolId, _snapshotId, ViewKind.Declaration, false);

                if (hasGeneratedSource != null && hasNonGeneratedSource == null)
                {
                    capsule.Uncertainties.Add(new UncertaintyEntry([symbolId], "generated_excluded", $"Generated symbol '{symbolId}' was excluded because includeGenerated is set to false. Review generated code if runtime behavior depends on it."));
                }
            }
        }

        private void PopulateSuggestedVerification(ContextCapsule capsule)
        {
            var incomingEdges = _edgeStore.GetIncomingEdges(_snapshotId, _symbolId.Value);

            foreach (var edge in incomingEdges)
            {
                if (edge.Kind != EdgeKind.TestedBy.ToString())
                    continue;

                var testInfo = _declarationStore.GetSymbolInfo(edge.SourceSymbolId, _snapshotId);
                var testName = testInfo?.FullyQualifiedName ?? edge.SourceSymbolId;

                capsule.SuggestedVerification.Add(new VerificationSuggestion(testId: edge.SourceSymbolId, testName: testName, description: $"Run '{testName}' to verify correctness after modifications."));
            }
        }
    }
}
