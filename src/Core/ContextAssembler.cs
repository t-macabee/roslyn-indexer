using Lurp.Storage;

namespace Lurp.Workspace
{
    public sealed record ContextLookup(
        string SnapshotId,
        string? SymbolArg,
        string? FileArg,
        int? LineNumber
    );

    public sealed record ContextAssemblyOptions(
        ContextIntent Intent,
        int Budget,
        int MaxHops = 3,
        bool IncludeGenerated = false
    );

    public sealed class ContextAssembler
    {
        public IEdgeStore EdgeStore { get; init; } = null!;
        public IDeclarationStore DeclarationStore { get; init; } = null!;
        public string SnapshotId { get; init; } = string.Empty;
        public SymbolId SymbolId { get; init; } = null!;
        public ContextIntent Intent { get; init; }
        public int Budget { get; init; }
        public int MaxHops { get; init; } = 3;
        public bool IncludeGenerated { get; init; }

        public ContextCapsule Assemble()
        {
            var anchor = BuildAnchor();
            var capsule = new ContextCapsule(anchor)
            {
                Budget = Budget,
            };

            int runningTotal = EstimateTokens(anchor.Source);
            bool truncated = false;
            var truncatedCategories = new List<string>();

            var tiers = GetTierOrder();

            foreach (var (build, name) in tiers)
            {
                var items = build();
                int tierCost = items.Sum(i => EstimateTokens(i.Source));

                if (runningTotal + tierCost <= Budget)
                {
                    AddTierToCapsule(capsule, name, items);
                    runningTotal += tierCost;
                }
                else
                {
                    foreach (var item in items)
                    {
                        int itemCost = EstimateTokens(item.Source);
                        if (runningTotal + itemCost > Budget)
                            break;
                        AddTierToCapsule(capsule, name, new List<CapsuleItem> { item });
                        runningTotal += itemCost;
                    }
                    truncated = true;
                    truncatedCategories.Add(name);
                    break;
                }
            }

            capsule.EstimatedTokens = runningTotal;
            capsule.Truncated = truncated;
            capsule.TruncatedCategories = truncatedCategories;

            PopulateUncertainties(capsule);
            PopulateSuggestedVerification(capsule);

            return capsule;
        }

        private (Func<List<CapsuleItem>> Build, string Name)[] GetTierOrder()
        {
            var defaultTiers = new (Func<List<CapsuleItem>> Build, string Name)[]
            {
                ((Func<List<CapsuleItem>>)BuildContracts, "contracts"),
                ((Func<List<CapsuleItem>>)BuildDirectCallees, "directCallees"),
                ((Func<List<CapsuleItem>>)BuildDirectCallers, "directCallers"),
                ((Func<List<CapsuleItem>>)BuildRegisteredImplementations, "registeredImplementations"),
                ((Func<List<CapsuleItem>>)BuildRelevantTests, "relevantTests"),
                ((Func<List<CapsuleItem>>)BuildSecondDegreeContext, "secondDegreeContext"),
                ((Func<List<CapsuleItem>>)BuildSurroundingSiblings, "surroundingSource"),
            };

            switch (Intent)
            {
                case ContextIntent.Inspect:
                    return new (Func<List<CapsuleItem>> Build, string Name)[]
                    {
                        ((Func<List<CapsuleItem>>)BuildContracts, "contracts"),
                        ((Func<List<CapsuleItem>>)BuildDirectCallees, "directCallees"),
                        ((Func<List<CapsuleItem>>)BuildDirectCallers, "directCallers"),
                        ((Func<List<CapsuleItem>>)BuildRegisteredImplementations, "registeredImplementations"),
                        ((Func<List<CapsuleItem>>)BuildSecondDegreeContext, "secondDegreeContext"),
                        ((Func<List<CapsuleItem>>)BuildRelevantTests, "relevantTests"),
                        ((Func<List<CapsuleItem>>)BuildSurroundingSiblings, "surroundingSource"),
                    };

                case ContextIntent.Modify:
                    return new (Func<List<CapsuleItem>> Build, string Name)[]
                    {
                        ((Func<List<CapsuleItem>>)BuildContracts, "contracts"),
                        ((Func<List<CapsuleItem>>)BuildDirectCallers, "directCallers"),
                        ((Func<List<CapsuleItem>>)BuildRegisteredImplementations, "registeredImplementations"),
                        ((Func<List<CapsuleItem>>)BuildRelevantTests, "relevantTests"),
                        ((Func<List<CapsuleItem>>)BuildDirectCallees, "directCallees"),
                        ((Func<List<CapsuleItem>>)BuildSecondDegreeContext, "secondDegreeContext"),
                        ((Func<List<CapsuleItem>>)BuildSurroundingSiblings, "surroundingSource"),
                    };

                case ContextIntent.Diagnose:
                    return new (Func<List<CapsuleItem>> Build, string Name)[]
                    {
                        ((Func<List<CapsuleItem>>)BuildDirectCallers, "directCallers"),
                        ((Func<List<CapsuleItem>>)BuildRegisteredImplementations, "registeredImplementations"),
                        ((Func<List<CapsuleItem>>)BuildContracts, "contracts"),
                        ((Func<List<CapsuleItem>>)BuildDirectCallees, "directCallees"),
                        ((Func<List<CapsuleItem>>)BuildRelevantTests, "relevantTests"),
                        ((Func<List<CapsuleItem>>)BuildSecondDegreeContext, "secondDegreeContext"),
                        ((Func<List<CapsuleItem>>)BuildSurroundingSiblings, "surroundingSource"),
                    };

                default:
                    return defaultTiers;
            }
        }

        private static void AddTierToCapsule(ContextCapsule capsule, string tierName, List<CapsuleItem> items)
        {
            switch (tierName)
            {
                case "contracts":
                    capsule.Contracts.AddRange(items);
                    break;
                case "directCallees":
                    capsule.DirectCallees.AddRange(items);
                    break;
                case "directCallers":
                    capsule.DirectCallers.AddRange(items);
                    break;
                case "registeredImplementations":
                    capsule.RegisteredImplementations.AddRange(items);
                    break;
                case "relevantTests":
                    capsule.RelevantTests.AddRange(items);
                    break;
                case "secondDegreeContext":
                    capsule.SecondDegreeContext.AddRange(items);
                    break;
                case "surroundingSource":
                    capsule.SurroundingSource.AddRange(items);
                    break;
            }
        }

        private CapsuleAnchor BuildAnchor()
        {
            var info = DeclarationStore.GetSymbolInfo(SymbolId.Value, SnapshotId);
            if (info == null)
            {
                throw new InvalidOperationException($"Symbol '{SymbolId.Value}' not found in snapshot '{SnapshotId}'.");
            }

            var source = DeclarationStore.GetSymbolSource(SymbolId.Value, SnapshotId, ViewKind.Declaration, IncludeGenerated);
            source ??= string.Empty;

            return new CapsuleAnchor(symbolId: SymbolId.Value,fullyQualifiedName: info.FullyQualifiedName ?? SymbolId.Value,kind: info.Kind.ToString(),
                source: source);
        }

        private List<CapsuleItem> BuildContracts()
        {
            var results = new List<CapsuleItem>();
            var edges = EdgeStore.GetOutgoingEdges(SnapshotId, SymbolId.Value);

            foreach (var edge in edges)
            {
                if (edge.Kind != EdgeKind.Implements.ToString() &&
                    edge.Kind != EdgeKind.Overrides.ToString())
                {
                    continue;
                }

                var item = BuildCapsuleItem(edge.TargetSymbolId, edge.Kind, edge.Provenance);
                if (item != null)
                {
                    results.Add(item);
                }
            }

            return results;
        }

        private List<CapsuleItem> BuildDirectCallees()
        {
            var results = new List<CapsuleItem>();
            var allowedKinds = new HashSet<string>
            {
                EdgeKind.Calls.ToString(),
                EdgeKind.Constructs.ToString()
            };

            var traverser = new ImpactTraverser(EdgeStore, SnapshotId);
            var paths = traverser.TraceImpact(symbolId: SymbolId.Value,direction: ImpactDirection.Downstream,allowedEdgeKinds: allowedKinds,maxDepth: 1);

            var seen = new HashSet<string>();
            foreach (var path in paths)
            {
                foreach (var hop in path.Hops)
                {
                    var neighborId = hop.TargetSymbolId;
                    if (!seen.Add(neighborId))
                        continue;

                    var item = BuildCapsuleItem(neighborId, hop.EdgeKind, hop.Provenance);
                    if (item != null)
                    {
                        results.Add(item);
                    }
                }
            }

            return results;
        }

        private List<CapsuleItem> BuildDirectCallers()
        {
            var results = new List<CapsuleItem>();
            var allowedKinds = new HashSet<string>
            {
                EdgeKind.Calls.ToString()
            };

            var traverser = new ImpactTraverser(EdgeStore, SnapshotId);
            var paths = traverser.TraceImpact(symbolId: SymbolId.Value,direction: ImpactDirection.Upstream,allowedEdgeKinds: allowedKinds,maxDepth: 1);

            var seen = new HashSet<string>();
            foreach (var path in paths)
            {
                foreach (var hop in path.Hops)
                {
                    var neighborId = hop.SourceSymbolId;
                    if (!seen.Add(neighborId))
                        continue;

                    var item = BuildCapsuleItem(neighborId, hop.EdgeKind, hop.Provenance);
                    if (item != null)
                    {
                        results.Add(item);
                    }
                }
            }

            var incomingEdges = EdgeStore.GetIncomingEdges(SnapshotId, SymbolId.Value);
            foreach (var edge in incomingEdges)
            {
                if (edge.Kind != EdgeKind.RoutesTo.ToString() &&
                    edge.Kind != EdgeKind.Handles.ToString())
                {
                    continue;
                }

                var sourceId = edge.SourceSymbolId;
                if (!seen.Add(sourceId))
                    continue;

                var item = BuildCapsuleItem(sourceId, edge.Kind, edge.Provenance);
                if (item != null)
                {
                    results.Add(item);
                }
            }

            return results;
        }

        private List<CapsuleItem> BuildRegisteredImplementations()
        {
            var results = new List<CapsuleItem>();
            var seen = new HashSet<string>();

            var incomingEdges = EdgeStore.GetIncomingEdges(SnapshotId, SymbolId.Value);
            foreach (var edge in incomingEdges)
            {
                if (edge.Kind != EdgeKind.MayDispatchTo.ToString() &&
                    edge.Kind != EdgeKind.Registers.ToString())
                {
                    continue;
                }

                var sourceId = edge.SourceSymbolId;
                if (!seen.Add(sourceId))
                    continue;

                var item = BuildCapsuleItem(sourceId, edge.Kind, edge.Provenance);
                if (item != null)
                {
                    results.Add(item);
                }
            }

            var outgoingEdges = EdgeStore.GetOutgoingEdges(SnapshotId, SymbolId.Value);
            foreach (var edge in outgoingEdges)
            {
                if (edge.Kind != EdgeKind.MayDispatchTo.ToString() &&
                    edge.Kind != EdgeKind.Handles.ToString() &&
                    edge.Kind != EdgeKind.Registers.ToString())
                {
                    continue;
                }

                var targetId = edge.TargetSymbolId;
                if (!seen.Add(targetId))
                    continue;

                var item = BuildCapsuleItem(targetId, edge.Kind, edge.Provenance);
                if (item != null)
                {
                    results.Add(item);
                }
            }

            return results;
        }

        private List<CapsuleItem> BuildRelevantTests()
        {
            var results = new List<CapsuleItem>();

            var incomingEdges = EdgeStore.GetIncomingEdges(SnapshotId, SymbolId.Value);
            foreach (var edge in incomingEdges)
            {
                if (edge.Kind != EdgeKind.TestedBy.ToString())
                    continue;

                var testSymbolId = edge.SourceSymbolId;
                var item = BuildCapsuleItem(testSymbolId, edge.Kind, edge.Provenance);
                if (item != null)
                {
                    results.Add(item);
                }
            }

            return results;
        }

        private List<CapsuleItem> BuildSecondDegreeContext()
        {
            var results = new List<CapsuleItem>();
            var allowedKinds = new HashSet<string>
            {
                EdgeKind.Calls.ToString()
            };

            if (MaxHops <= 1)
                return results;

            var traverser = new ImpactTraverser(EdgeStore, SnapshotId);
            var paths = traverser.TraceImpact(symbolId: SymbolId.Value,direction: ImpactDirection.Upstream,allowedEdgeKinds: allowedKinds,maxDepth: MaxHops);

            var seen = new HashSet<string>();
            foreach (var path in paths)
            {
                foreach (var hop in path.Hops)
                {
                    var neighborId = hop.SourceSymbolId;
                    if (!seen.Add(neighborId))
                        continue;

                    if (neighborId == SymbolId.Value)
                        continue;

                    var item = BuildCapsuleItem(neighborId, hop.EdgeKind, hop.Provenance);
                    if (item != null)
                    {
                        results.Add(item);
                    }
                }
            }

            return results;
        }

        private List<CapsuleItem> BuildSurroundingSiblings()
        {
            var results = new List<CapsuleItem>();

            var incomingEdges = EdgeStore.GetIncomingEdges(SnapshotId, SymbolId.Value);
            string? parentId = null;
            foreach (var edge in incomingEdges)
            {
                if (edge.Kind == EdgeKind.Contains.ToString())
                {
                    parentId = edge.SourceSymbolId;
                    break;
                }
            }

            if (parentId == null)
                return results;

            var parentEdges = EdgeStore.GetOutgoingEdges(SnapshotId, parentId);
            foreach (var edge in parentEdges)
            {
                if (edge.Kind != EdgeKind.Contains.ToString())
                    continue;

                var siblingId = edge.TargetSymbolId;
                if (siblingId == SymbolId.Value)
                    continue;

                var item = BuildCapsuleItem(siblingId, EdgeKind.Contains.ToString(), edge.Provenance);
                if (item != null)
                {
                    results.Add(item);
                }
            }

            return results;
        }

        private CapsuleItem? BuildCapsuleItem(string symbolId, string edgeKind, string provenance)
        {
            var info = DeclarationStore.GetSymbolInfo(symbolId, SnapshotId);
            if (info == null)
                return null;

            var source = DeclarationStore.GetSymbolSource(symbolId, SnapshotId, ViewKind.Declaration, IncludeGenerated);

            if (!IncludeGenerated && source == null)
            {
                var hasGeneratedOnly = DeclarationStore.GetSymbolSource(symbolId, SnapshotId, ViewKind.Declaration, true) != null;
                if (hasGeneratedOnly)
                    return null;
            }

            return new CapsuleItem(symbolId: symbolId,kind: info.Kind.ToString(),
                fullyQualifiedName: info.FullyQualifiedName ?? symbolId,
                provenance: provenance,
                edgeKind: edgeKind,
                source: source);
        }

        private void PopulateUncertainties(ContextCapsule capsule)
        {
            var neighborhood = new HashSet<string> { SymbolId.Value };

            void CollectFromItems(IEnumerable<CapsuleItem> items)
            {
                foreach (var item in items)
                    neighborhood.Add(item.SymbolId);
            }

            CollectFromItems(capsule.Contracts);
            CollectFromItems(capsule.DirectCallees);
            CollectFromItems(capsule.DirectCallers);
            CollectFromItems(capsule.RegisteredImplementations);
            CollectFromItems(capsule.RelevantTests);
            CollectFromItems(capsule.SecondDegreeContext);
            CollectFromItems(capsule.SurroundingSource);

            var anchorEdges = EdgeStore.GetIncomingEdges(SnapshotId, SymbolId.Value)
                .Concat(EdgeStore.GetOutgoingEdges(SnapshotId, SymbolId.Value))
                .ToList();

            foreach (var edge in anchorEdges)
            {
                neighborhood.Add(edge.SourceSymbolId);
                neighborhood.Add(edge.TargetSymbolId);
            }

            foreach (var symbolId in neighborhood)
            {
                var edges = EdgeStore.GetIncomingEdges(SnapshotId, symbolId)
                    .Concat(EdgeStore.GetOutgoingEdges(SnapshotId, symbolId));

                foreach (var edge in edges)
                {
                    if (edge.Kind == EdgeKind.ReflectionNameCandidate.ToString())
                    {
                        capsule.Uncertainties.Add(new UncertaintyEntry(new List<string> { edge.SourceSymbolId, edge.TargetSymbolId },edge.Kind,$"Reflection name candidate: the string-based reference to '{edge.TargetSymbolId}' was matched by name. Verify that this reference correctly resolves at runtime."));
                    }
                    else if (edge.Kind == EdgeKind.ReflectionTargetUnknown.ToString())
                    {
                        capsule.Uncertainties.Add(new UncertaintyEntry(new List<string> { edge.SourceSymbolId, edge.TargetSymbolId },edge.Kind,"Unknown reflection target: the runtime target of this reflection call cannot be statically determined."));
                    }
                }
            }

            foreach (var symbolId in neighborhood)
            {
                var outgoing = EdgeStore.GetOutgoingEdges(SnapshotId, symbolId);
                foreach (var edge in outgoing)
                {
                    if (edge.Kind != EdgeKind.MayDispatchTo.ToString())
                        continue;
                    if (edge.Provenance == "compiler_proved" || edge.Provenance == "framework_derived")
                        continue;

                    capsule.Uncertainties.Add(new UncertaintyEntry(new List<string> { edge.SourceSymbolId, edge.TargetSymbolId },edge.Kind,$"Dispatch candidate '{edge.TargetSymbolId}' was resolved with evidence level '{edge.Provenance}'. Manually verify that the runtime dispatch reaches the correct implementation."));
                }
            }

            var frameworkKinds = new HashSet<string>
            {
                EdgeKind.RoutesTo.ToString(),
                EdgeKind.Handles.ToString(),
                EdgeKind.Registers.ToString()
            };

            foreach (var symbolId in neighborhood)
            {
                var edges = EdgeStore.GetIncomingEdges(SnapshotId, symbolId)
                    .Concat(EdgeStore.GetOutgoingEdges(SnapshotId, symbolId));

                foreach (var edge in edges)
                {
                    if (!frameworkKinds.Contains(edge.Kind))
                        continue;
                    if (edge.Provenance != "convention")
                        continue;

                    capsule.Uncertainties.Add(new UncertaintyEntry(new List<string> { edge.SourceSymbolId, edge.TargetSymbolId },edge.Kind,$"Convention-based framework binding: the '{edge.Kind}' edge was inferred by naming convention, not explicit registration. Verify that the expected target is reached at runtime."));
                }
            }

            if (!IncludeGenerated)
            {
                foreach (var symbolId in neighborhood)
                {
                    var hasGeneratedSource = DeclarationStore.GetSymbolSource(symbolId, SnapshotId, ViewKind.Declaration, true);
                    var hasNonGeneratedSource = DeclarationStore.GetSymbolSource(symbolId, SnapshotId, ViewKind.Declaration, false);

                    if (hasGeneratedSource != null && hasNonGeneratedSource == null)
                    {
                        capsule.Uncertainties.Add(new UncertaintyEntry(new List<string> { symbolId },"generated_excluded",$"Generated symbol '{symbolId}' was excluded because includeGenerated is set to false. Review generated code if runtime behavior depends on it."));
                    }
                }
            }
        }

        private void PopulateSuggestedVerification(ContextCapsule capsule)
        {
            var incomingEdges = EdgeStore.GetIncomingEdges(SnapshotId, SymbolId.Value);

            foreach (var edge in incomingEdges)
            {
                if (edge.Kind != EdgeKind.TestedBy.ToString())
                    continue;

                var testInfo = DeclarationStore.GetSymbolInfo(edge.SourceSymbolId, SnapshotId);
                var testName = testInfo?.FullyQualifiedName ?? edge.SourceSymbolId;

                capsule.SuggestedVerification.Add(new VerificationSuggestion(testId: edge.SourceSymbolId,testName: testName,description: $"Run '{testName}' to verify correctness after modifications."));
            }
        }

        private static int EstimateTokens(string? text)
        {
            return (text ?? string.Empty).Length / 4;
        }

        public static ContextCapsule ResolveAndAssemble(IIndexStore store, ContextLookup lookup, ContextAssemblyOptions options)
        {
            if (!string.IsNullOrEmpty(lookup.SymbolArg))
            {
                var symbolId = SymbolId.Parse(lookup.SymbolArg!);
                var assembler = new ContextAssembler
                {
                    EdgeStore = store,
                    DeclarationStore = store,
                    SnapshotId = lookup.SnapshotId,
                    SymbolId = symbolId,
                    Intent = options.Intent,
                    Budget = options.Budget,
                    MaxHops = options.MaxHops,
                    IncludeGenerated = options.IncludeGenerated,
                };
                return assembler.Assemble();
            }

            var resolvedId = store.ResolveSymbolByLocation(lookup.FileArg!, lookup.LineNumber!.Value, lookup.SnapshotId, options.IncludeGenerated);

            if (resolvedId == null)
            {
                var gapAnchor = new CapsuleAnchor(
                    symbolId: $"file://{lookup.FileArg}:{lookup.LineNumber}",
                    fullyQualifiedName: $"<no symbol at {lookup.FileArg}:{lookup.LineNumber}>",
                    kind: "gap",
                    source: string.Empty);

                var gapCapsule = new ContextCapsule(gapAnchor)
                {
                    Budget = options.Budget,
                    EstimatedTokens = 0,
                    Truncated = false,
                };

                gapCapsule.Uncertainties.Add(new UncertaintyEntry(
                    new List<string> { gapAnchor.SymbolId },
                    "location_gap",
                    $"No symbol found at {lookup.FileArg}:{lookup.LineNumber}. The location may be in a comment, whitespace, or within a region not represented in the index."));

                return gapCapsule;
            }

            var resolvedSymbolId = SymbolId.Parse(resolvedId);
            var resolvedAssembler = new ContextAssembler
            {
                EdgeStore = store,
                DeclarationStore = store,
                SnapshotId = lookup.SnapshotId,
                SymbolId = resolvedSymbolId,
                Intent = options.Intent,
                Budget = options.Budget,
                MaxHops = options.MaxHops,
                IncludeGenerated = options.IncludeGenerated,
            };
            return resolvedAssembler.Assemble();
        }
    }
}
