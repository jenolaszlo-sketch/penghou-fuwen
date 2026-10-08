using Penghou.Fuwen;

namespace Penghou.Fuwen.Zhinu;

/// <summary>How a durable step came to exist.</summary>
public enum FuwenZhinuStepOrigin
{
    /// <summary>A step that corresponds to a declared Fuwen node.</summary>
    Declared = 0,

    /// <summary>An execution-only step the port synthesizes (merge, fallback, loop control, protocol turn).</summary>
    Synthetic = 1,
}

/// <summary>The Fuwen/Zhinu role of one durable step.</summary>
public enum FuwenZhinuStepKind
{
    /// <summary>A declared activity node.</summary>
    Activity = 0,
    /// <summary>A declared context node.</summary>
    Context = 1,
    /// <summary>A declared inference node's primary step.</summary>
    Inference = 2,
    /// <summary>A declared checkpoint node.</summary>
    Checkpoint = 3,
    /// <summary>A declared wait node.</summary>
    Wait = 4,
    /// <summary>A declared return node.</summary>
    Return = 5,
    /// <summary>A conditional's condition step (top level only).</summary>
    Condition = 6,
    /// <summary>A synthetic conditional-merge step.</summary>
    ConditionalMerge = 7,
    /// <summary>A synthetic inference-fallback step.</summary>
    InferenceFallback = 8,
    /// <summary>A fan-out aggregate step.</summary>
    FanOutAggregate = 9,
    /// <summary>A fan-out item step (covers the whole item body).</summary>
    FanOutItem = 10,
    /// <summary>A durable loop control step (limits/limit/condition/commit).</summary>
    LoopControl = 11,
    /// <summary>A coordinated-inference protocol loop step (model turn or read tool).</summary>
    InferenceProtocol = 12,
}

/// <summary>A raw step-key scope: an enclosing durable loop or a fan-out item.</summary>
public enum FuwenZhinuStepScopeKind
{
    /// <summary>A durable loop scope segment.</summary>
    Loop = 0,
    /// <summary>A fan-out item scope segment.</summary>
    FanOutItem = 1,
}

/// <summary>One scope segment extracted from a persisted step key.</summary>
public sealed class FuwenZhinuStepScope
{
    /// <summary>Initializes a scope segment.</summary>
    public FuwenZhinuStepScope(FuwenZhinuStepScopeKind kind, string name, int? iteration = null, string? itemToken = null)
    {
        Kind = kind;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Iteration = iteration;
        ItemToken = itemToken;
    }

    /// <summary>Gets the scope kind.</summary>
    public FuwenZhinuStepScopeKind Kind { get; }
    /// <summary>Gets the loop or fan-out name.</summary>
    public string Name { get; }
    /// <summary>Gets the loop iteration, or null for a fan-out item.</summary>
    public int? Iteration { get; }
    /// <summary>Gets the fan-out item token, or null for a loop segment.</summary>
    public string? ItemToken { get; }
}

/// <summary>
/// The plan-scoped identity of one durable step the port can emit. Exactly one
/// of <see cref="ExactStepKey"/>, the loop-body pair
/// (<see cref="LoopNames"/> + <see cref="BodyStepName"/>), or
/// <see cref="FanOutNodePath"/> describes how the step key is formed.
/// </summary>
public sealed class FuwenZhinuStepDescriptor
{
    /// <summary>Initializes a step descriptor.</summary>
    public FuwenZhinuStepDescriptor(
        string? declaredNodePath,
        IReadOnlyList<string> coveredDeclaredNodePaths,
        FuwenZhinuStepOrigin origin,
        FuwenZhinuStepKind kind,
        string? exactStepKey,
        IReadOnlyList<string> loopNames,
        string? bodyStepName,
        string? fanOutNodePath)
    {
        DeclaredNodePath = declaredNodePath;
        CoveredDeclaredNodePaths = coveredDeclaredNodePaths ?? throw new ArgumentNullException(nameof(coveredDeclaredNodePaths));
        Origin = origin;
        Kind = kind;
        ExactStepKey = exactStepKey;
        LoopNames = loopNames ?? throw new ArgumentNullException(nameof(loopNames));
        BodyStepName = bodyStepName;
        FanOutNodePath = fanOutNodePath;
    }

    /// <summary>Gets the declared Fuwen node path, or null for a purely synthetic step.</summary>
    public string? DeclaredNodePath { get; }
    /// <summary>Gets the declared node paths this step covers (fan-out item bodies).</summary>
    public IReadOnlyList<string> CoveredDeclaredNodePaths { get; }
    /// <summary>Gets whether the step is declared or synthetic.</summary>
    public FuwenZhinuStepOrigin Origin { get; }
    /// <summary>Gets the step kind.</summary>
    public FuwenZhinuStepKind Kind { get; }
    /// <summary>Gets the exact key for a non-parameterized step, else null.</summary>
    public string? ExactStepKey { get; }
    /// <summary>Gets the enclosing loop-name chain for a loop-body step, else empty.</summary>
    public IReadOnlyList<string> LoopNames { get; }
    /// <summary>Gets the body step name for a loop-body step, else null.</summary>
    public string? BodyStepName { get; }
    /// <summary>Gets the fan-out node path for a fan-out item descriptor, else null.</summary>
    public string? FanOutNodePath { get; }
}

/// <summary>A declared/synthetic execution identity matched from a persisted step key.</summary>
public sealed class FuwenZhinuStepMatch
{
    /// <summary>Initializes a step match.</summary>
    public FuwenZhinuStepMatch(FuwenZhinuStepDescriptor descriptor, IReadOnlyList<FuwenZhinuStepScope> scope)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
    }

    /// <summary>Gets the matched descriptor.</summary>
    public FuwenZhinuStepDescriptor Descriptor { get; }
    /// <summary>Gets the runtime scope (loop iterations, fan-out token) extracted from the key.</summary>
    public IReadOnlyList<FuwenZhinuStepScope> Scope { get; }
}

/// <summary>
/// A plan-scoped, read-only description of every durable step the Fuwen to
/// Zhinu port can emit for a compiled plan, together with an authoritative
/// matcher that classifies persisted step keys without any consumer-side
/// parsing. Bind the map to a run via
/// <see cref="PlanRevision"/> and <see cref="ExecutionFingerprint"/>.
/// </summary>
public sealed class FuwenZhinuStepMap
{
    /// <summary>Initializes a step map.</summary>
    public FuwenZhinuStepMap(
        string schemaVersion,
        string planRevision,
        string executionFingerprint,
        IReadOnlyList<FuwenZhinuStepDescriptor> steps)
    {
        SchemaVersion = schemaVersion ?? throw new ArgumentNullException(nameof(schemaVersion));
        PlanRevision = planRevision ?? throw new ArgumentNullException(nameof(planRevision));
        ExecutionFingerprint = executionFingerprint ?? throw new ArgumentNullException(nameof(executionFingerprint));
        Steps = steps ?? throw new ArgumentNullException(nameof(steps));
    }

    /// <summary>Gets the map schema version.</summary>
    public string SchemaVersion { get; }
    /// <summary>Gets the source plan revision.</summary>
    public string PlanRevision { get; }
    /// <summary>Gets the source plan execution fingerprint.</summary>
    public string ExecutionFingerprint { get; }
    /// <summary>Gets every declared and synthetic step the port can emit.</summary>
    public IReadOnlyList<FuwenZhinuStepDescriptor> Steps { get; }
}

/// <summary>
/// Builds <see cref="FuwenZhinuStepMap"/> from a compiled plan and matches
/// persisted step keys back to declared/synthetic execution identity. All key
/// construction shares <see cref="FuwenZhinuStepKeys"/>, so execution, mapping,
/// and matching cannot drift.
/// </summary>
/// <remarks>
/// This type owns the Fuwen-to-Zhinu adapter key contract. Because Zhinu's
/// durable-loop key helpers are internal, compatibility is guarded by exactness
/// tests that execute real <c>WorkflowEngine</c> runs and assert every persisted
/// <c>WorkflowStepRun.StepKey</c> is matched; a change to Zhinu's key
/// conventions must fail those tests, never silently change this mapper.
/// </remarks>
public static class FuwenZhinuStepMapper
{
    /// <summary>Schema version of the emitted map. Additive onward.</summary>
    public const string CurrentSchemaVersion = "fuwen-zhinu-step-map-v1";

    /// <summary>Builds the plan-scoped step map.</summary>
    public static FuwenZhinuStepMap Map(WorkflowPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var steps = new List<FuwenZhinuStepDescriptor>();
        Walk(plan.Nodes, [], innermostRepeatPath: null, inFanOut: false, steps);
        return new FuwenZhinuStepMap(
            CurrentSchemaVersion,
            plan.Revision,
            WorkflowPlanIdentity.ComputeExecutionFingerprint(plan),
            steps.AsReadOnly());
    }

    /// <summary>
    /// Classifies a persisted step key using only the map. Never parses a key
    /// for the caller: the caller passes the opaque <c>WorkflowStepRun.StepKey</c>
    /// and receives its declared/synthetic identity.
    /// </summary>
    public static bool TryMatchStepKey(FuwenZhinuStepMap map, string stepKey, out FuwenZhinuStepMatch match)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        match = null!;

        // 1. Non-parameterized steps carry their exact key.
        foreach (var descriptor in map.Steps)
        {
            if (descriptor.ExactStepKey is { } exact && string.Equals(exact, stepKey, StringComparison.Ordinal))
            {
                match = new FuwenZhinuStepMatch(descriptor, []);
                return true;
            }
        }

        // 2. Durable loop envelope: $loop/{name}/{iter}[/loop/{name}/{iter}].../terminal
        if (stepKey.StartsWith("$loop/", StringComparison.Ordinal))
        {
            return TryMatchLoopKey(map, stepKey, out match);
        }

        // 3. Fan-out item steps: <fanOutPath>/$item/sha256-<64 hex>.
        foreach (var descriptor in map.Steps)
        {
            if (descriptor.FanOutNodePath is { } fanOutPath)
            {
                var prefix = fanOutPath + "/$item/sha256-";
                if (stepKey.Length == prefix.Length + 64 && stepKey.StartsWith(prefix, StringComparison.Ordinal))
                {
                    match = new FuwenZhinuStepMatch(
                        descriptor,
                        [new FuwenZhinuStepScope(FuwenZhinuStepScopeKind.FanOutItem, fanOutPath, itemToken: stepKey[prefix.Length..])]);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the exact persisted key for a parameterized descriptor given
    /// concrete scope instances. Optional convenience for execution and tests;
    /// read-side consumers should use <see cref="TryMatchStepKey"/>.
    /// </summary>
    public static string ResolveStepKey(FuwenZhinuStepDescriptor descriptor, IReadOnlyList<FuwenZhinuStepScope> scope)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(scope);
        if (descriptor.ExactStepKey is { } exact)
        {
            return exact;
        }

        if (descriptor.FanOutNodePath is { } fanOutPath)
        {
            var item = scope.FirstOrDefault(segment => segment.Kind == FuwenZhinuStepScopeKind.FanOutItem)
                ?? throw new ArgumentException("A fan-out item token is required to resolve this step key.", nameof(scope));
            return $"{fanOutPath}/$item/{item.ItemToken}";
        }

        if (descriptor.BodyStepName is { } bodyStep)
        {
            var loopPath = ResolveLoopPath(descriptor.LoopNames, scope);
            var iteration = scope.Last(segment => segment.Kind == FuwenZhinuStepScopeKind.Loop).Iteration
                ?? throw new ArgumentException("An iteration is required to resolve this step key.", nameof(scope));
            return FuwenZhinuStepKeys.LoopBody(loopPath, iteration, bodyStep);
        }

        throw new ArgumentException("The descriptor has no resolvable step key form.", nameof(descriptor));
    }

    private static string ResolveLoopPath(IReadOnlyList<string> loopNames, IReadOnlyList<FuwenZhinuStepScope> scope)
    {
        var loops = scope.Where(segment => segment.Kind == FuwenZhinuStepScopeKind.Loop).ToArray();
        if (loops.Length != loopNames.Count)
        {
            throw new ArgumentException("The scope does not match the descriptor's loop nesting.", nameof(scope));
        }

        var path = FuwenZhinuStepKeys.RootLoopPath(loopNames[0]);
        for (var index = 1; index < loopNames.Count; index++)
        {
            path = FuwenZhinuStepKeys.NestedLoopPath(path, loops[index - 1].Iteration ?? 0, loopNames[index]);
        }

        return path;
    }

    private static bool TryMatchLoopKey(FuwenZhinuStepMap map, string stepKey, out FuwenZhinuStepMatch match)
    {
        match = null!;
        var segments = stepKey.Split('/');
        // segments[0] == "$loop"
        var names = new List<string>();
        var iterations = new List<int>();
        var scope = new List<FuwenZhinuStepScope>();
        var index = 1;
        while (index < segments.Length)
        {
            var name = segments[index++];
            if (name.Length == 0 || name is "loop" or "body" or "condition" or "commit" or "limits" or "limit")
            {
                return false;
            }

            names.Add(name);
            if (index >= segments.Length)
            {
                // A nested loop's final step key ends at "loop/{name}" with no iteration or terminal.
                return MatchLoopControl(map, names, iterations, scope, "final", out match);
            }

            var next = segments[index];
            if (next is FuwenZhinuStepKeys.LimitsSegment or FuwenZhinuStepKeys.LimitSegment)
            {
                // Scope-level control step: $loop/{name}/limits
                return MatchLoopControl(map, names, iterations, scope, next, out match);
            }

            if (!int.TryParse(next, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var iteration) || iteration < 1)
            {
                return false;
            }

            iterations.Add(iteration);
            scope.Add(new FuwenZhinuStepScope(FuwenZhinuStepScopeKind.Loop, name, iteration));
            index++;

            if (index >= segments.Length)
            {
                return false;
            }

            var terminal = segments[index];
            switch (terminal)
            {
                case FuwenZhinuStepKeys.ConditionSegment:
                case FuwenZhinuStepKeys.CommitSegment:
                    index++;
                    if (index != segments.Length)
                    {
                        return false;
                    }

                    return MatchLoopControl(map, names, iterations, scope, terminal, out match);
                case FuwenZhinuStepKeys.LoopSegment:
                    index++;
                    continue;
                case FuwenZhinuStepKeys.BodySegment:
                    index++;
                    if (index != segments.Length - 1)
                    {
                        return false;
                    }

                    return MatchLoopBody(map, names, scope, segments[index], out match);
                default:
                    return false;
            }
        }

        return false;
    }

    private static bool MatchLoopBody(
        FuwenZhinuStepMap map,
        IReadOnlyList<string> names,
        IReadOnlyList<FuwenZhinuStepScope> scope,
        string bodyStepName,
        out FuwenZhinuStepMatch match)
    {
        match = null!;
        foreach (var descriptor in map.Steps)
        {
            if (!LoopNamesEqual(descriptor.LoopNames, names))
            {
                continue;
            }

            if (descriptor.Kind == FuwenZhinuStepKind.InferenceProtocol
                && IsProtocolBodyStep(bodyStepName))
            {
                match = new FuwenZhinuStepMatch(descriptor, scope);
                return true;
            }

            if (descriptor.BodyStepName is { } body && string.Equals(body, bodyStepName, StringComparison.Ordinal))
            {
                match = new FuwenZhinuStepMatch(descriptor, scope);
                return true;
            }
        }

        return false;
    }

    private static bool MatchLoopControl(
        FuwenZhinuStepMap map,
        IReadOnlyList<string> names,
        IReadOnlyList<int> iterations,
        IReadOnlyList<FuwenZhinuStepScope> scope,
        string control,
        out FuwenZhinuStepMatch match)
    {
        match = null!;
        foreach (var descriptor in map.Steps)
        {
            if (descriptor.Kind == FuwenZhinuStepKind.LoopControl && LoopNamesEqual(descriptor.LoopNames, names))
            {
                match = new FuwenZhinuStepMatch(descriptor, scope);
                return true;
            }
        }

        return false;
    }

    private static bool LoopNamesEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsProtocolBodyStep(string stepName) =>
        stepName.StartsWith("infer-model-turn-", StringComparison.Ordinal)
        || stepName.StartsWith("infer-read-tool-", StringComparison.Ordinal);

    private static void Walk(
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<string> loopNames,
        string? innermostRepeatPath,
        bool inFanOut,
        List<FuwenZhinuStepDescriptor> sink)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case RepeatNode repeat:
                    var childLoops = Append(loopNames, repeat.Name);
                    sink.Add(LoopControlDescriptor(childLoops, repeat.StructuralPath));
                    if (childLoops.Count == 1)
                    {
                        sink.Add(LoopFinal(repeat.Name));
                    }

                    Walk(repeat.Body, childLoops, repeat.StructuralPath, inFanOut, sink);
                    break;

                case ConditionalNode conditional:
                    if (innermostRepeatPath is null)
                    {
                        sink.Add(Simple(conditional.StructuralPath, FuwenZhinuStepKind.Condition, FuwenZhinuStepKeys.Node(conditional.StructuralPath)));
                    }

                    Walk(conditional.Then, loopNames, innermostRepeatPath, inFanOut, sink);
                    Walk(conditional.Else, loopNames, innermostRepeatPath, inFanOut, sink);
                    if (conditional.Merge is not null)
                    {
                        if (innermostRepeatPath is null)
                        {
                            sink.Add(SyntheticStep(
                                conditional.StructuralPath,
                                FuwenZhinuStepKind.ConditionalMerge,
                                FuwenZhinuStepKeys.ConditionalMerge(conditional.StructuralPath)));
                        }
                        else
                        {
                            sink.Add(LoopBodyDescriptor(
                                conditional.StructuralPath,
                                FuwenZhinuStepOrigin.Synthetic,
                                FuwenZhinuStepKind.ConditionalMerge,
                                loopNames,
                                FuwenZhinuStepKeys.RepeatMergeStepName(conditional.StructuralPath, innermostRepeatPath)));
                        }
                    }

                    break;

                case FanOutNode fanOut:
                    sink.Add(Simple(fanOut.StructuralPath, FuwenZhinuStepKind.FanOutAggregate, FuwenZhinuStepKeys.FanOutAggregate(fanOut.StructuralPath)));
                    sink.Add(new FuwenZhinuStepDescriptor(
                        fanOut.StructuralPath,
                        Flatten(fanOut.Body).Select(static node => node.StructuralPath).ToArray(),
                        FuwenZhinuStepOrigin.Declared,
                        FuwenZhinuStepKind.FanOutItem,
                        null,
                        loopNames,
                        null,
                        fanOut.StructuralPath));
                    // Fan-out body nodes are subsumed by the item step; they do not emit their own steps.
                    break;

                default:
                    WalkLeaf(node, loopNames, innermostRepeatPath, sink);
                    break;
            }
        }
    }

    private static void WalkLeaf(
        WorkflowNode node,
        IReadOnlyList<string> loopNames,
        string? innermostRepeatPath,
        List<FuwenZhinuStepDescriptor> sink)
    {
        var kind = node switch
        {
            ActivityNode => FuwenZhinuStepKind.Activity,
            ContextNode => FuwenZhinuStepKind.Context,
            InferenceNode => FuwenZhinuStepKind.Inference,
            CheckpointNode => FuwenZhinuStepKind.Checkpoint,
            WaitNode => FuwenZhinuStepKind.Wait,
            ReturnNode => FuwenZhinuStepKind.Return,
            _ => (FuwenZhinuStepKind?)null,
        };
        if (kind is null)
        {
            return;
        }

        if (innermostRepeatPath is null)
        {
            sink.Add(Simple(node.StructuralPath, kind.Value, FuwenZhinuStepKeys.Node(node.StructuralPath)));
        }
        else
        {
            sink.Add(LoopBodyDescriptor(
                node.StructuralPath,
                FuwenZhinuStepOrigin.Declared,
                kind.Value,
                loopNames,
                FuwenZhinuStepKeys.RepeatBodyStepName(node.StructuralPath, innermostRepeatPath)));
        }

        // Inference extras. Fallback is only stepped at the top level by the port.
        if (node is InferenceNode inference)
        {
            if (inference.FailureFallback is not null && innermostRepeatPath is null)
            {
                sink.Add(SyntheticStep(node.StructuralPath, FuwenZhinuStepKind.InferenceFallback, FuwenZhinuStepKeys.InferenceFallback(node.StructuralPath)));
            }

            if (inference.Protocol is not null)
            {
                var protocolName = FuwenZhinuStepKeys.ProtocolLoopName(node.StructuralPath);
                var protocolLoops = Append(loopNames, protocolName);
                sink.Add(new FuwenZhinuStepDescriptor(
                    node.StructuralPath,
                    [],
                    FuwenZhinuStepOrigin.Synthetic,
                    FuwenZhinuStepKind.InferenceProtocol,
                    null,
                    protocolLoops,
                    null,
                    null));
                sink.Add(LoopControlDescriptor(protocolLoops));
                if (protocolLoops.Count == 1)
                {
                    sink.Add(LoopFinal(protocolName));
                }
            }
        }
    }

    private static FuwenZhinuStepDescriptor Simple(string declaredPath, FuwenZhinuStepKind kind, string exactKey) =>
        new(declaredPath, [], FuwenZhinuStepOrigin.Declared, kind, exactKey, [], null, null);

    private static FuwenZhinuStepDescriptor SyntheticStep(string declaredPath, FuwenZhinuStepKind kind, string exactKey) =>
        new(declaredPath, [], FuwenZhinuStepOrigin.Synthetic, kind, exactKey, [], null, null);

    private static FuwenZhinuStepDescriptor LoopBodyDescriptor(
        string declaredPath,
        FuwenZhinuStepOrigin origin,
        FuwenZhinuStepKind kind,
        IReadOnlyList<string> loopNames,
        string bodyStepName) =>
        new(declaredPath, [], origin, kind, null, loopNames, bodyStepName, null);

    private static FuwenZhinuStepDescriptor LoopControlDescriptor(IReadOnlyList<string> loopNames, string? declaredNodePath = null) =>
        new(declaredNodePath, [], FuwenZhinuStepOrigin.Synthetic, FuwenZhinuStepKind.LoopControl, null, loopNames, null, null);

    private static FuwenZhinuStepDescriptor LoopFinal(string loopName) =>
        new(null, [], FuwenZhinuStepOrigin.Synthetic, FuwenZhinuStepKind.LoopControl, loopName, [], null, null);

    private static IReadOnlyList<string> Append(IReadOnlyList<string> names, string value)
    {
        var copy = new string[names.Count + 1];
        for (var index = 0; index < names.Count; index++)
        {
            copy[index] = names[index];
        }

        copy[names.Count] = value;
        return copy;
    }

    private static IEnumerable<WorkflowNode> Flatten(IEnumerable<WorkflowNode> source)
    {
        foreach (var node in source)
        {
            yield return node;
            var children = node switch
            {
                ConditionalNode conditional => conditional.Then.Concat(conditional.Else),
                FanOutNode fanOut => fanOut.Body,
                RepeatNode repeat => repeat.Body,
                _ => Enumerable.Empty<WorkflowNode>(),
            };
            foreach (var child in Flatten(children))
            {
                yield return child;
            }
        }
    }
}
