using System.Security.Cryptography;
using System.Text;

namespace Penghou.Fuwen;

/// <summary>Normalizes canonical formatter output and derives authored-source identity.</summary>
public static class SourceIdentity
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Normalizes transport details without changing comments, whitespace,
    /// string contents, or Unicode code points chosen by the formatter.
    /// </summary>
    public static string NormalizeFormattedSource(string formattedSource)
    {
        ArgumentNullException.ThrowIfNull(formattedSource);
        var normalized = formattedSource.TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n');
        if (normalized.IndexOf('\0') >= 0)
            throw new ArgumentException("Canonical source cannot contain NUL characters.", nameof(formattedSource));
        _ = StrictUtf8.GetByteCount(normalized);
        return normalized + "\n";
    }

    /// <summary>Hashes normalized canonical formatter output using fuwen-source/v1.</summary>
    public static string ComputeSourceFingerprint(string formattedSource)
    {
        var normalized = NormalizeFormattedSource(formattedSource);
        var bytes = StrictUtf8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return $"sha256:{FuwenContracts.SourceFingerprintVersion}:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
