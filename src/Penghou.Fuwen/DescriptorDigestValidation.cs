namespace Penghou.Fuwen;

/// <summary>Validates the current descriptor-admission digest identity contract.</summary>
internal static class DescriptorDigestValidation
{
    internal const string CanonicalAlgorithm = "sha256";
    internal const int MaximumContractLength = 64;
    internal const int CanonicalValueLength = 64;

    internal static void Validate(ContentDigest digest, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(digest, parameterName);

        if (!string.Equals(digest.Algorithm, CanonicalAlgorithm, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Descriptor content digests must use the canonical '{CanonicalAlgorithm}' algorithm.",
                parameterName);

        if (string.IsNullOrWhiteSpace(digest.Contract))
            throw new ArgumentException("Descriptor content digest contract cannot be empty or whitespace.", parameterName);
        if (digest.Contract.Length > MaximumContractLength)
            throw new ArgumentException(
                $"Descriptor content digest contract cannot exceed {MaximumContractLength} characters.",
                parameterName);

        if (digest.Value is null || digest.Value.Length != CanonicalValueLength)
            throw new ArgumentException(
                $"Descriptor content digest value must contain exactly {CanonicalValueLength} lowercase hexadecimal characters.",
                parameterName);

        foreach (var character in digest.Value)
        {
            var isLowerHex = character is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isLowerHex)
                throw new ArgumentException(
                    $"Descriptor content digest value must contain exactly {CanonicalValueLength} lowercase hexadecimal characters.",
                    parameterName);
        }
    }
}
