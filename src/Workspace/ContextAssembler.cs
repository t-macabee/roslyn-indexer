using Lurp.Storage;

namespace Lurp.Workspace
{
    internal sealed record ContextLookup(
        string SnapshotId,
        string? SymbolArg,
        string? FileArg,
        int? LineNumber
    );

    internal sealed record ContextAssemblyOptions(
        ContextIntent Intent,
        int Budget,
        int MaxHops = 3,
        bool IncludeGenerated = false
    );

    internal sealed class ContextAssembler
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
            var context = new ContextTierContext(EdgeStore, DeclarationStore, SnapshotId, SymbolId, MaxHops, IncludeGenerated);
            var anchor = BuildAnchor();
            var capsule = new ContextCapsule(anchor)
            {
                Budget = Budget,
            };

            int runningTotal = EstimateTokens(anchor.Source);
            bool truncated = false;
            var truncatedCategories = new List<string>();

            foreach (var tier in GetTierBuilders(context))
            {
                var items = tier.Build();
                int tierCost = items.Sum(i => EstimateTokens(i.Source));

                if (runningTotal + tierCost <= Budget)
                {
                    AddTierToCapsule(capsule, tier.Name, items);
                    runningTotal += tierCost;
                }
                else
                {
                    foreach (var item in items)
                    {
                        int itemCost = EstimateTokens(item.Source);
                        if (runningTotal + itemCost > Budget)
                            break;
                        AddTierToCapsule(capsule, tier.Name, [item]);
                        runningTotal += itemCost;
                    }
                    truncated = true;
                    truncatedCategories.Add(tier.Name);
                    break;
                }
            }

            capsule.EstimatedTokens = runningTotal;
            capsule.Truncated = truncated;
            capsule.TruncatedCategories = truncatedCategories;

            new UncertaintyDetector(EdgeStore, DeclarationStore, SnapshotId, SymbolId, IncludeGenerated)
                .Detect(capsule);

            return capsule;
        }

        private List<IContextTierBuilder> GetTierBuilders(ContextTierContext context)
        {
            IContextTierBuilder contracts = new ContractsTierBuilder(context);
            IContextTierBuilder directCallees = new DirectCalleesTierBuilder(context);
            IContextTierBuilder directCallers = new DirectCallersTierBuilder(context);
            IContextTierBuilder registeredImplementations = new RegisteredImplementationsTierBuilder(context);
            IContextTierBuilder relevantTests = new RelevantTestsTierBuilder(context);
            IContextTierBuilder secondDegreeContext = new SecondDegreeContextTierBuilder(context);
            IContextTierBuilder surroundingSiblings = new SurroundingSiblingsTierBuilder(context);

            return Intent switch
            {
                ContextIntent.Inspect =>
                [
                    contracts, directCallees, directCallers, registeredImplementations,
                    secondDegreeContext, relevantTests, surroundingSiblings,
                ],

                ContextIntent.Modify =>
                [
                    contracts, directCallers, registeredImplementations, relevantTests,
                    directCallees, secondDegreeContext, surroundingSiblings,
                ],

                ContextIntent.Diagnose =>
                [
                    directCallers, registeredImplementations, contracts, directCallees,
                    relevantTests, secondDegreeContext, surroundingSiblings,
                ],

                _ =>
                [
                    contracts, directCallees, directCallers, registeredImplementations,
                    relevantTests, secondDegreeContext, surroundingSiblings,
                ],
            };
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

            return new CapsuleAnchor(symbolId: SymbolId.Value, fullyQualifiedName: info.FullyQualifiedName ?? SymbolId.Value, kind: info.Kind.ToString(),
                source: source);
        }

        private static int EstimateTokens(string? text)
        {
            return (text ?? string.Empty).Length / 4;
        }

        public static ContextCapsule ResolveAndAssemble(IEdgeStore edgeStore, IDeclarationStore declarationStore, ContextLookup lookup, ContextAssemblyOptions options)
        {
            if (!string.IsNullOrEmpty(lookup.SymbolArg))
            {
                var symbolId = SymbolId.Parse(lookup.SymbolArg!);
                var assembler = new ContextAssembler
                {
                    EdgeStore = edgeStore,
                    DeclarationStore = declarationStore,
                    SnapshotId = lookup.SnapshotId,
                    SymbolId = symbolId,
                    Intent = options.Intent,
                    Budget = options.Budget,
                    MaxHops = options.MaxHops,
                    IncludeGenerated = options.IncludeGenerated,
                };
                return assembler.Assemble();
            }

            var resolvedId = declarationStore.ResolveSymbolByLocation(lookup.FileArg!, lookup.LineNumber!.Value, lookup.SnapshotId, options.IncludeGenerated);

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
                    [gapAnchor.SymbolId],
                    "location_gap",
                    $"No symbol found at {lookup.FileArg}:{lookup.LineNumber}. The location may be in a comment, whitespace, or within a region not represented in the index."));

                return gapCapsule;
            }

            var resolvedSymbolId = SymbolId.Parse(resolvedId);
            var resolvedAssembler = new ContextAssembler
            {
                EdgeStore = edgeStore,
                DeclarationStore = declarationStore,
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
