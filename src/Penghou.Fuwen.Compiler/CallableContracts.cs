using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler;

/// <summary>A bounded named input accepted by a trusted callable descriptor.</summary>
/// <param name="Name">The exact ordinal argument name.</param>
/// <param name="Type">The trusted nominal or primitive input type.</param>
public sealed record CallableParameter(string Name, FuwenType Type);

/// <summary>The exact input and output types exposed by a trusted callable descriptor.</summary>
/// <param name="Parameters">The required named parameters. Omission is not supported in the first contract.</param>
/// <param name="OutputType">The exact value type returned by the callable.</param>
public sealed record CallableSignature(IReadOnlyList<CallableParameter> Parameters, FuwenType OutputType);

/// <summary>The declared side-effect class of a trusted callable descriptor.</summary>
public enum CallableEffect
{
    /// <summary>No externally observable effect.</summary>
    None = 0,
    /// <summary>Reads state without intentionally changing it.</summary>
    Read = 1,
    /// <summary>Changes externally observable state.</summary>
    Write = 2,
    /// <summary>Invokes an external system with effects not represented more narrowly.</summary>
    External = 3,
    /// <summary>Performs an operation whose loss or duplication may be destructive.</summary>
    Destructive = 4,
}

/// <summary>The declared duplicate-invocation behavior of a trusted callable descriptor.</summary>
public enum CallableIdempotency
{
    /// <summary>Repeating the same effective invocation does not add another effect.</summary>
    Idempotent = 0,
    /// <summary>Idempotency requires an operation key supplied by a future execution adapter.</summary>
    IdempotentWithKey = 1,
    /// <summary>Repeating the invocation may add another effect.</summary>
    NonIdempotent = 2,
    /// <summary>The duplicate-invocation behavior has not been established.</summary>
    Unknown = 3,
}

/// <summary>The declared retry behavior of a trusted callable descriptor.</summary>
public enum CallableRetrySafety
{
    /// <summary>The catalogue attests that retry is safe under the declared idempotency contract.</summary>
    Safe = 0,
    /// <summary>The callable must not be retried automatically.</summary>
    Unsafe = 1,
    /// <summary>A future host execution policy must decide retry safety.</summary>
    HostControlled = 2,
    /// <summary>Retry behavior has not been established.</summary>
    Unknown = 3,
}

/// <summary>A bounded trusted callable contract used for semantic compilation.
/// It does not grant execution authority and is not an admission receipt.</summary>
/// <param name="Signature">The exact trusted callable signature.</param>
/// <param name="Effect">The independently declared effect class.</param>
/// <param name="Idempotency">The independently declared duplicate-invocation behavior.</param>
/// <param name="RetrySafety">The independently declared retry behavior.</param>
public sealed record CallableContract(
    CallableSignature Signature,
    CallableEffect Effect,
    CallableIdempotency Idempotency,
    CallableRetrySafety RetrySafety);
