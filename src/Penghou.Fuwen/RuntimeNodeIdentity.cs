using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Penghou.Fuwen;

/// <summary>A deterministic typed scalar used to identify a durable fan-out item.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(StringRuntimeKey), "string")]
[JsonDerivedType(typeof(IntegerRuntimeKey), "integer")]
[JsonDerivedType(typeof(EnumRuntimeKey), "enum")]
[JsonDerivedType(typeof(UuidRuntimeKey), "uuid")]
[JsonDerivedType(typeof(PositionalRuntimeKey), "position")]
public abstract record RuntimeIdentityKey;

/// <summary>A bounded ordinal string key.</summary>
public sealed record StringRuntimeKey(string Value) : RuntimeIdentityKey;

/// <summary>A signed 64-bit integer key.</summary>
public sealed record IntegerRuntimeKey(long Value) : RuntimeIdentityKey;

/// <summary>A nominal enum member key.</summary>
public sealed record EnumRuntimeKey(DescriptorReference EnumSchema, string Value) : RuntimeIdentityKey;

/// <summary>A UUID key serialized as a lowercase canonical D-format string.</summary>
public sealed record UuidRuntimeKey(string Value) : RuntimeIdentityKey;

/// <summary>An explicit opt-in to order-sensitive positional fan-out identity.</summary>
public sealed record PositionalRuntimeKey(long Index) : RuntimeIdentityKey;

/// <summary>Derives deterministic runtime node identities from structural paths.</summary>
public static class RuntimeNodeIdentity
{
    /// <summary>The maximum UTF-8 length of string and enum key values.</summary>
    public const int MaximumKeyUtf8Bytes = 256;

    /// <summary>Returns canonical typed key bytes used by equality and identity derivation.</summary>
    public static byte[] GetCanonicalKeyBytes(RuntimeIdentityKey key)
    {
        ValidateKey(key);
        return CanonicalJson.Serialize(key);
    }

    /// <summary>Returns a fixed safe path segment derived from canonical typed key bytes.</summary>
    public static string GetKeyPathSegment(RuntimeIdentityKey key) =>
        $"sha256-{Convert.ToHexString(SHA256.HashData(GetCanonicalKeyBytes(key))).ToLowerInvariant()}";

    /// <summary>Rejects duplicate canonical keys before fan-out child execution.</summary>
    public static void ValidateUniqueKeys(IEnumerable<RuntimeIdentityKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var canonicalKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var encoded = Convert.ToBase64String(GetCanonicalKeyBytes(key));
            if (!canonicalKeys.Add(encoded))
                throw new ArgumentException("Fan-out item keys must be unique within one invocation.", nameof(keys));
        }
    }

    /// <summary>Creates an intrinsic sequential loop-instance identity.</summary>
    public static string CreateIteration(string loopStructuralPath, int iteration, params string[] descendantSegments)
    {
        if (iteration < 0)
            throw new ArgumentOutOfRangeException(nameof(iteration));
        return AppendRuntimePath(loopStructuralPath, ["$iteration", iteration.ToString(System.Globalization.CultureInfo.InvariantCulture)], descendantSegments);
    }

    /// <summary>Creates a stable keyed fan-out item identity.</summary>
    public static string CreateFanOutItem(string fanOutStructuralPath, RuntimeIdentityKey key, params string[] descendantSegments) =>
        AppendRuntimePath(fanOutStructuralPath, ["$item", GetKeyPathSegment(key)], descendantSegments);

    private static string AppendRuntimePath(string structuralPath, IReadOnlyList<string> runtimeSegments, IReadOnlyList<string> descendants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(structuralPath);
        ArgumentNullException.ThrowIfNull(descendants);
        foreach (var descendant in descendants)
            StructuralNodeIdentity.ValidateSegment(descendant);

        var path = string.Join('/', new[] { structuralPath }.Concat(runtimeSegments).Concat(descendants));
        if (Encoding.UTF8.GetByteCount(path) > StructuralNodeIdentity.MaximumPathUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(descendants), $"Runtime path exceeds {StructuralNodeIdentity.MaximumPathUtf8Bytes} UTF-8 bytes.");
        return path;
    }

    private static void ValidateKey(RuntimeIdentityKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        switch (key)
        {
            case StringRuntimeKey text:
                ValidateBoundedText(text.Value, nameof(text.Value));
                break;
            case IntegerRuntimeKey:
                break;
            case EnumRuntimeKey member:
                if (member.EnumSchema.Kind != DescriptorKind.Schema)
                    throw new ArgumentException("An enum runtime key must reference a schema descriptor.", nameof(key));
                ValidateBoundedText(member.Value, nameof(member.Value));
                break;
            case UuidRuntimeKey uuid when !Guid.TryParseExact(uuid.Value, "D", out var parsed) ||
                                         !string.Equals(parsed.ToString("D"), uuid.Value, StringComparison.Ordinal):
                throw new ArgumentException("A UUID runtime key must use lowercase canonical D format.", nameof(key));
            case UuidRuntimeKey:
                break;
            case PositionalRuntimeKey position when position.Index < 0:
                throw new ArgumentOutOfRangeException(nameof(key), "A positional runtime key cannot be negative.");
            case PositionalRuntimeKey:
                break;
            default:
                throw new NotSupportedException($"Unsupported runtime key type '{key.GetType().Name}'.");
        }
    }

    private static void ValidateBoundedText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Encoding.UTF8.GetByteCount(value) > MaximumKeyUtf8Bytes)
            throw new ArgumentOutOfRangeException(parameterName, $"Runtime key exceeds {MaximumKeyUtf8Bytes} UTF-8 bytes.");
    }
}
