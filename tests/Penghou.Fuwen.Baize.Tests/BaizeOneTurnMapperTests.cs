using FluentAssertions;
using Penghou.Baize;
using Penghou.Fuwen;
using Penghou.Fuwen.Baize;

namespace Penghou.Fuwen.Baize.Tests;

public sealed class BaizeOneTurnMapperTests
{
    private const int MaxArgs = 1024;

    [Fact]
    public void Normalized_success_maps_to_exact_descriptors_and_args_in_order()
    {
        var emit = Descriptor(DescriptorKind.Tool, "emit");
        var search = Descriptor(DescriptorKind.Tool, "search");
        var declared = new[]
        {
            new BaizeToolBinding(emit, new LlmTool("emit", "emit", "{\"type\":\"string\"}")),
            new BaizeToolBinding(search, new LlmTool("search", "search", "{\"type\":\"string\"}")),
        };
        var visible = new[]
        {
            new InferenceToolRequirement(emit),
            new InferenceToolRequirement(search),
        };
        var calls = new[]
        {
            Call("c-1", "emit", "{\"a\":1}"),
            Call("c-2", "search", "{}"),
        };

        var (proposals, failure) = BaizeOneTurnMapper.MapToolCalls(calls, declared, visible, MaxArgs);

        failure.Should().BeNull();
        proposals.Should().NotBeNull();
        proposals!.Should().HaveCount(2);
        proposals[0].CallId.Should().Be("c-1");
        proposals[0].Tool.Should().Be(emit);
        proposals[0].ArgumentsJson.Should().Be("{\"a\":1}");
        proposals[1].CallId.Should().Be("c-2");
        proposals[1].Tool.Should().Be(search);
        proposals[1].ArgumentsJson.Should().Be("{}");
    }

    [Fact]
    public void UnknownTool_status_is_a_tool_mapping_failure()
    {
        var emit = Descriptor(DescriptorKind.Tool, "emit");
        var calls = new[] { Call("c-1", "emit", "{}", LlmToolCallNormalizationStatus.UnknownTool) };

        var (proposals, failure) = BaizeOneTurnMapper.MapToolCalls(calls, Declared(emit), Visible(emit), MaxArgs);

        proposals.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Theory]
    [InlineData(LlmToolCallNormalizationStatus.EmptyArguments)]
    [InlineData(LlmToolCallNormalizationStatus.InvalidArguments)]
    public void Bad_argument_statuses_are_tool_mapping_failures(LlmToolCallNormalizationStatus status)
    {
        var emit = Descriptor(DescriptorKind.Tool, "emit");
        var calls = new[] { Call("c-1", "emit", "{}", status) };

        var (proposals, failure) = BaizeOneTurnMapper.MapToolCalls(calls, Declared(emit), Visible(emit), MaxArgs);

        proposals.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Fact]
    public void Undeclared_provider_name_is_a_tool_mapping_failure()
    {
        var emit = Descriptor(DescriptorKind.Tool, "emit");
        var calls = new[] { Call("c-1", "ghost", "{}") };

        var (proposals, failure) = BaizeOneTurnMapper.MapToolCalls(calls, Declared(emit), Visible(emit), MaxArgs);

        proposals.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Fact]
    public void Duplicate_ids_are_a_tool_mapping_failure_mentioning_identity()
    {
        var emit = Descriptor(DescriptorKind.Tool, "emit");
        var calls = new[]
        {
            Call("dup", "emit", "{}"),
            Call("dup", "emit", "{}"),
        };

        var (proposals, failure) = BaizeOneTurnMapper.MapToolCalls(calls, Declared(emit), Visible(emit), MaxArgs);

        proposals.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
        failure.Message.Should().Contain("dup");
    }

    [Fact]
    public void Oversized_args_are_a_tool_mapping_failure()
    {
        var emit = Descriptor(DescriptorKind.Tool, "emit");
        var calls = new[] { Call("c-1", "emit", "{\"a\":12345}") };

        var (proposals, failure) = BaizeOneTurnMapper.MapToolCalls(calls, Declared(emit), Visible(emit), maximumArgumentBytes: 8);

        proposals.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[1,2]")]
    public void Mistyped_args_are_tool_mapping_failures(string argumentsJson)
    {
        var emit = Descriptor(DescriptorKind.Tool, "emit");
        var calls = new[] { Call("c-1", "emit", argumentsJson) };

        var (proposals, failure) = BaizeOneTurnMapper.MapToolCalls(calls, Declared(emit), Visible(emit), MaxArgs);

        proposals.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Fact]
    public void Null_list_is_a_tool_mapping_failure()
    {
        var emit = Descriptor(DescriptorKind.Tool, "emit");

        var (proposals, failure) = BaizeOneTurnMapper.MapToolCalls(null, Declared(emit), Visible(emit), MaxArgs);

        proposals.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Fact]
    public void Empty_list_is_a_tool_mapping_failure()
    {
        var emit = Descriptor(DescriptorKind.Tool, "emit");

        var (proposals, failure) = BaizeOneTurnMapper.MapToolCalls([], Declared(emit), Visible(emit), MaxArgs);

        proposals.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Fact]
    public void Missing_or_blank_identity_fields_are_tool_mapping_failures_not_throws()
    {
        var emit = Descriptor(DescriptorKind.Tool, "emit");
        var variants = new LlmToolCall[]
        {
            new(null!, "emit", "{}", false, null, LlmToolCallNormalizationStatus.Normalized, null),
            new("c-1", null!, "{}", false, null, LlmToolCallNormalizationStatus.Normalized, null),
            new("c-1", "emit", null!, false, null, LlmToolCallNormalizationStatus.Normalized, null),
            new("  ", "emit", "{}", false, null, LlmToolCallNormalizationStatus.Normalized, null),
            new("c-1", "emit", "", false, null, LlmToolCallNormalizationStatus.Normalized, null),
            new("c-\tb", "emit", "{}", false, null, LlmToolCallNormalizationStatus.Normalized, null),
        };

        foreach (var call in variants)
        {
            var (proposals, failure) = BaizeOneTurnMapper.MapToolCalls([call], Declared(emit), Visible(emit), MaxArgs);

            proposals.Should().BeNull();
            failure.Should().NotBeNull();
            failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
        }
    }

    [Fact]
    public void Null_entry_is_a_tool_mapping_failure()
    {
        var emit = Descriptor(DescriptorKind.Tool, "emit");
        var calls = new LlmToolCall[] { null! };

        var (proposals, failure) = BaizeOneTurnMapper.MapToolCalls(calls, Declared(emit), Visible(emit), MaxArgs);

        proposals.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Fact]
    public void Seventeen_calls_are_a_tool_mapping_failure()
    {
        var emit = Descriptor(DescriptorKind.Tool, "emit");
        var calls = Enumerable.Range(0, 17).Select(i => Call($"c-{i}", "emit", "{}")).ToArray();

        var (proposals, failure) = BaizeOneTurnMapper.MapToolCalls(calls, Declared(emit), Visible(emit), MaxArgs);

        proposals.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Fact]
    public void Write_capable_visible_requirement_is_a_tool_mapping_failure()
    {
        var emit = Descriptor(DescriptorKind.Tool, "emit");
        var declared = Declared(emit);
        var visible = new[] { new InferenceToolRequirement(emit, InferenceToolEffect.IdempotentWrite) };
        var calls = new[] { Call("c-1", "emit", "{}") };

        var (proposals, failure) = BaizeOneTurnMapper.MapToolCalls(calls, declared, visible, MaxArgs);

        proposals.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Fact]
    public void MapUsage_null_gives_all_null_usage()
    {
        var usage = BaizeOneTurnMapper.MapUsage(null);

        usage.Should().NotBeNull();
        usage.PromptTokens.Should().BeNull();
        usage.CompletionTokens.Should().BeNull();
        usage.TotalTokens.Should().BeNull();
    }

    [Fact]
    public void MapUsage_carries_reported_token_counts()
    {
        var usage = BaizeOneTurnMapper.MapUsage(new LlmUsage(2, 3, 5));

        usage.PromptTokens.Should().Be(2);
        usage.CompletionTokens.Should().Be(3);
        usage.TotalTokens.Should().Be(5);
    }

    [Fact]
    public void Mapping_is_deterministic_without_provider_io()
    {
        var emit = Descriptor(DescriptorKind.Tool, "emit");
        var declared = Declared(emit);
        var visible = Visible(emit);
        var first = new[] { Call("c-1", "emit", "{\"a\":1}") };
        var second = new[] { Call("c-1", "emit", "{\"a\":1}") };

        var (firstProposals, firstFailure) = BaizeOneTurnMapper.MapToolCalls(first, declared, visible, MaxArgs);
        var (secondProposals, secondFailure) = BaizeOneTurnMapper.MapToolCalls(second, declared, visible, MaxArgs);

        firstFailure.Should().BeNull();
        secondFailure.Should().BeNull();
        firstProposals.Should().NotBeNull();
        secondProposals.Should().NotBeNull();
        secondProposals!.Select(p => (p.CallId, p.Tool, p.ArgumentsJson))
            .Should().Equal(firstProposals!.Select(p => (p.CallId, p.Tool, p.ArgumentsJson)));
    }

    private static LlmToolCall Call(
        string id,
        string name,
        string argumentsJson,
        LlmToolCallNormalizationStatus status = LlmToolCallNormalizationStatus.Normalized) =>
        new(id, name, argumentsJson, false, null, status, null);

    private static BaizeToolBinding[] Declared(DescriptorReference descriptor) =>
        [new(descriptor, new LlmTool(descriptor.Name, descriptor.Name, "{\"type\":\"string\"}"))];

    private static InferenceToolRequirement[] Visible(DescriptorReference descriptor) =>
        [new(descriptor)];

    private static DescriptorReference Descriptor(DescriptorKind kind, string name) =>
        new(kind, name, "1", new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
}
