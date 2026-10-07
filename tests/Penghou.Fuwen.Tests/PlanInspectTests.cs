using FluentAssertions;
using Fuwen.Inspect;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Tests;

/// <summary>
/// Plan inspection over real files: integrity verification, structural
/// validation, and declared-structure explanation, including tamper and
/// missing-file refusal. The v3 case derives an execution-intent plan from
/// the golden v1 bytes using core record copies only.
/// </summary>
public sealed class PlanInspectTests
{
    [Fact]
    public void Verify_AcceptsGoldenPlanWithComputedFingerprint()
    {
        var bytes = File.ReadAllBytes(GoldenPath());
        var plan = CanonicalJson.Deserialize<WorkflowPlan>(bytes);
        var document = WorkflowDefinitionDocument.Create(plan);
        var path = WriteTemp(document.CanonicalBytes.ToArray());

        try
        {
            var record = PlanInspector.Verify(path, document.ExecutionFingerprint);
            record.Status.Should().Be("Succeeded");
            record.ExecutionFingerprint.Should().Be(document.ExecutionFingerprint);
            record.CanonicalBytes.Should().Be(document.CanonicalBytes.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Verify_RejectsWrongFingerprintAndTamperedBytes()
    {
        var bytes = File.ReadAllBytes(GoldenPath());
        var plan = CanonicalJson.Deserialize<WorkflowPlan>(bytes);
        var document = WorkflowDefinitionDocument.Create(plan);
        var canonical = document.CanonicalBytes.ToArray();

        var wrongPath = WriteTemp(canonical);
        var tampered = (byte[])canonical.Clone();
        tampered[tampered.Length / 2] ^= 0xFF;
        var tamperedPath = WriteTemp(tampered);

        try
        {
            PlanInspector.Verify(
                wrongPath, "sha256:fuwen-execution/v1:" + new string('0', 64)).Status.Should().Be("Failed");
            var tamper = PlanInspector.Verify(tamperedPath, document.ExecutionFingerprint);
            tamper.Status.Should().Be("Failed");
            tamper.Reason.Should().NotBeNullOrEmpty();
        }
        finally
        {
            File.Delete(wrongPath);
            File.Delete(tamperedPath);
        }
    }

    [Fact]
    public void Validate_AcceptsGoldenPlanAndRejectsGarbage()
    {
        var valid = PlanInspector.Validate(GoldenPath());
        valid.Status.Should().Be("Succeeded");
        valid.NodeCount.Should().Be(4);

        var garbagePath = WriteTemp("{\"not\":\"a plan\"}"u8.ToArray());
        try
        {
            var invalid = PlanInspector.Validate(garbagePath);
            invalid.Status.Should().Be("Failed");
            invalid.Reason.Should().NotBeNullOrEmpty();
        }
        finally
        {
            File.Delete(garbagePath);
        }
    }

    [Fact]
    public void Explain_ProjectsDeclaredStructure()
    {
        var record = PlanInspector.Explain(GoldenPath());
        record.Status.Should().Be("Succeeded");
        record.Plan.Should().Be("answer");
        record.IrVersion.Should().Be("fuwen-ir/v1");
        record.ExecutionFingerprint.Should().NotBeNullOrEmpty();
        record.Nodes.Should().Contain(node =>
            node.Path == "answer/validate" && node.Kind == "activity" &&
            node.Descriptor == "sample.validate@1" && node.Intent == "-");
        record.Nodes.Should().Contain(node => node.Path == "answer/infer" && node.Kind == "inference");
        record.Descriptors.Should().Contain(descriptor =>
            descriptor.Kind == "Activity" && descriptor.Name == "sample.validate");
        record.Capabilities.Should().Contain(capability => capability.Name == "inference");
        record.RegionCount.Should().Be(1);
    }

    [Fact]
    public void Explain_ProjectsExecutionIntent()
    {
        var bytes = File.ReadAllBytes(GoldenPath());
        var golden = CanonicalJson.Deserialize<WorkflowPlan>(bytes);
        var nodes = golden.Nodes
            .Select(node => node is ActivityNode activity && activity.StructuralPath == "answer/validate"
                ? activity with
                {
                    ExecutionIntent = new ActivityExecutionIntent(
                        "diagnostic.whoami",
                        [new ExecutionGuarantee("execution.unit-termination", ExecutionGuaranteeLevel.Partial)],
                        [])
                }
                : node)
            .ToList();
        var derived = golden with
        {
            IrVersion = FuwenContracts.ExecutionIntentIrVersion,
            Nodes = nodes
        };
        var path = WriteTemp(WorkflowDefinitionDocument.Create(derived).CanonicalBytes.ToArray());

        try
        {
            var record = PlanInspector.Explain(path);
            record.Status.Should().Be("Succeeded");
            record.IrVersion.Should().Be("fuwen-ir/v3-execution-intent");
            record.Nodes.Should().Contain(node =>
                node.Path == "answer/validate" &&
                node.Intent.Contains("diagnostic.whoami", StringComparison.Ordinal) &&
                node.Intent.Contains("execution.unit-termination", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFileFailsClosed()
    {
        var missing = Path.Combine(Path.GetTempPath(), "fuwen-inspect-" + Guid.NewGuid().ToString("N") + ".json");
        PlanInspector.Verify(missing, "sha256:fuwen-execution/v1:" + new string('0', 64)).Status.Should().Be("Failed");
        PlanInspector.Validate(missing).Status.Should().Be("Failed");
        PlanInspector.Explain(missing).Status.Should().Be("Failed");
    }

    private static string GoldenPath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "golden", "workflow_plan_v1.json");
        File.Exists(candidate).Should().BeTrue("the golden plan fixture must be deployed with the tests.");
        return candidate;
    }

    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "fuwen-inspect-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
