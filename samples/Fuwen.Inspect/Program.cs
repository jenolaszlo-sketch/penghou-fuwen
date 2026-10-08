using System.Text.Json;
using Fuwen.Inspect;

if (args.Length == 0 || args[0] == "help")
{
    Console.WriteLine("Fuwen.Inspect (read-only plan inspection). Commands:");
    Console.WriteLine("verify <plan.json> <fingerprint> (integrity against the claimed fingerprint)");
    Console.WriteLine("validate <plan.json> (structural invariants; not admission)");
    Console.WriteLine("explain <plan.json> (declared structure; pins and capabilities as declared)");
    Console.WriteLine("compare <before-plan.json> <before-revision.json> <before-revision-fingerprint>");
    Console.WriteLine("        <after-plan.json> <after-revision.json> <after-revision-fingerprint>");
    Console.WriteLine("        (explanatory differences; never authorizes reuse)");
    return 0;
}

try
{
    InspectRecord record = args switch
    {
        ["verify", var plan, var fingerprint] => PlanInspector.Verify(plan, fingerprint),
        ["validate", var plan] => PlanInspector.Validate(plan),
        ["explain", var plan] => PlanInspector.Explain(plan),
        ["compare", var beforePlan, var beforeRevision, var beforeFingerprint,
            var afterPlan, var afterRevision, var afterFingerprint] =>
            PlanInspector.Compare(
                beforePlan, beforeRevision, beforeFingerprint,
                afterPlan, afterRevision, afterFingerprint),
        _ => throw new ArgumentException("Unknown command or arguments.")
    };
    Console.WriteLine(JsonSerializer.Serialize(record, record.GetType()));
    return record.Status == "Succeeded" ? 0 : 2;
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "DeniedOrUnavailable" }));
    return 2;
}
