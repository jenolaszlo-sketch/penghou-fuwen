using System.Text.Json;
using Fuwen.Inspect;

if (args.Length == 0 || args[0] == "help")
{
    Console.WriteLine("Fuwen.Inspect (read-only plan inspection). Commands:");
    Console.WriteLine("verify <plan.json> <fingerprint> (integrity against the claimed fingerprint)");
    Console.WriteLine("validate <plan.json> (structural invariants; not admission)");
    Console.WriteLine("explain <plan.json> (declared structure; pins and capabilities as declared)");
    return 0;
}

try
{
    InspectRecord record = args switch
    {
        ["verify", var plan, var fingerprint] => PlanInspector.Verify(plan, fingerprint),
        ["validate", var plan] => PlanInspector.Validate(plan),
        ["explain", var plan] => PlanInspector.Explain(plan),
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
