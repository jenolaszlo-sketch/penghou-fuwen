using System.Text.Json;

namespace Penghou.Fuwen.Zhinu;

/// <summary>Evaluates admitted condition operators over representation-neutral runtime values.</summary>
internal static class FuwenConditionEvaluator
{
    internal static bool Evaluate(ConditionOperator op, RuntimeValue left, RuntimeValue? right)
    {
        ArgumentNullException.ThrowIfNull(left);
        var leftJson = RuntimeValueJson.ToJsonElement(left);
        switch (op)
        {
            case ConditionOperator.Exists:
                return leftJson.ValueKind != JsonValueKind.Null;
            case ConditionOperator.Not:
                return !ReadBoolean(leftJson, "not");
            case ConditionOperator.And:
                return ReadBoolean(leftJson, "and") && ReadBoolean(ToJson(right, op), "and");
            case ConditionOperator.Or:
                return ReadBoolean(leftJson, "or") || ReadBoolean(ToJson(right, op), "or");
            case ConditionOperator.Equal:
                return ScalarEqual(leftJson, ToJson(right, op));
            case ConditionOperator.NotEqual:
                return !ScalarEqual(leftJson, ToJson(right, op));
            case ConditionOperator.LessThan:
                return Compare(leftJson, ToJson(right, op), "less-than") < 0;
            case ConditionOperator.LessThanOrEqual:
                return Compare(leftJson, ToJson(right, op), "less-than-or-equal") <= 0;
            case ConditionOperator.GreaterThan:
                return Compare(leftJson, ToJson(right, op), "greater-than") > 0;
            case ConditionOperator.GreaterThanOrEqual:
                return Compare(leftJson, ToJson(right, op), "greater-than-or-equal") >= 0;
            default:
                throw new FuwenZhinuAdapterException($"Condition operator '{op}' is unsupported.");
        }
    }

    private static JsonElement ToJson(RuntimeValue? value, ConditionOperator op) =>
        RuntimeValueJson.ToJsonElement(value ?? throw new FuwenZhinuExecutionException($"Condition '{op}' requires a right operand."));

    private static bool ScalarEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind &&
            (left.ValueKind != JsonValueKind.Number || right.ValueKind != JsonValueKind.Number))
            return false;
        switch (left.ValueKind)
        {
            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                if (left.TryGetDecimal(out var leftNumber) && right.TryGetDecimal(out var rightNumber))
                    return leftNumber == rightNumber;
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                return left.GetBoolean() == right.GetBoolean();
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return true;
        }
        return CanonicalJson.Canonicalize(left).AsSpan().SequenceEqual(CanonicalJson.Canonicalize(right));
    }

    private static int Compare(JsonElement left, JsonElement right, string operation)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number &&
            left.TryGetDecimal(out var leftNumber) && right.TryGetDecimal(out var rightNumber))
            return leftNumber.CompareTo(rightNumber);
        if (left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String)
            return string.CompareOrdinal(left.GetString(), right.GetString());
        throw new FuwenZhinuExecutionException($"Condition '{operation}' requires two comparable strings or finite numbers.");
    }

    private static bool ReadBoolean(JsonElement value, string operation) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FuwenZhinuExecutionException($"Condition '{operation}' requires Boolean operands."),
    };
}
