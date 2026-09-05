using System.Text;
using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class SourceIdentityTests
{
    [Fact]
    public void Transport_normalization_removes_BOM_normalizes_lines_and_final_newlines()
    {
        var windows = "\uFEFFworkflow answer {\r\n}\r\n\r\n";
        var unix = "workflow answer {\n}\n";

        SourceIdentity.NormalizeFormattedSource(windows).Should().Be(unix);
        SourceIdentity.ComputeSourceFingerprint(windows)
            .Should().Be(SourceIdentity.ComputeSourceFingerprint(unix));
    }

    [Fact]
    public void Comments_and_unicode_code_points_are_preserved_as_authored_identity()
    {
        var composed = "// café\nworkflow answer {\n}\n";
        var decomposed = "// cafe\u0301\nworkflow answer {\n}\n";
        var withoutComment = "workflow answer {\n}\n";

        SourceIdentity.ComputeSourceFingerprint(composed)
            .Should().NotBe(SourceIdentity.ComputeSourceFingerprint(decomposed));
        SourceIdentity.ComputeSourceFingerprint(composed)
            .Should().NotBe(SourceIdentity.ComputeSourceFingerprint(withoutComment));
    }

    [Fact]
    public void Source_fingerprint_has_a_stable_contract_envelope()
    {
        SourceIdentity.ComputeSourceFingerprint("workflow answer {\n}\n")
            .Should().StartWith("sha256:fuwen-source/v1:").And.HaveLength(87);
    }

    [Fact]
    public void Invalid_utf16_is_rejected_instead_of_replaced()
    {
        var invalid = "workflow \ud800";

        var act = () => SourceIdentity.ComputeSourceFingerprint(invalid);

        act.Should().Throw<EncoderFallbackException>();
    }
}
