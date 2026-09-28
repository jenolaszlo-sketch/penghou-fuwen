namespace Penghou.Fuwen;

/// <summary>A selected turn executor's normalized failure and commitment assessment.</summary>
public sealed class InferenceTurnFailureException : Exception
{
    /// <summary>Creates a failure that the coordinator can preserve without interpreting provider text.</summary>
    public InferenceTurnFailureException(ExecutionFailure failure)
        : base((failure ?? throw new ArgumentNullException(nameof(failure))).Message)
    {
        Failure = failure;
    }

    /// <summary>The bounded failure, including whether remote work may have committed.</summary>
    public ExecutionFailure Failure { get; }
}
