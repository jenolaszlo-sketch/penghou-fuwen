using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class CanonicalJsonPortabilityTests
{
    public static IEnumerable<object[]> CrossLanguageGoldenVectors =>
    [
        ["{\"value\":0.1234567890123456789012345678}", "{\"value\":0.1234567890123456789012345678}", "d115ce19f5f21df14db1b11c4f459bb8d5c7f990482a65d0b17ef7fa84ac2fcc"],
        ["{\"value\":79228162514264337593543950335}", "{\"value\":79228162514264337593543950335}", "5a8fa6b318b0e9294bff0600107886cc87081660d6a9522827818c478ab3b99f"],
        ["{\"tiny\":1e-28,\"huge\":1e+28,\"zero\":-0.0,\"equivalent\":1.2300e+3}", "{\"equivalent\":1230,\"huge\":10000000000000000000000000000,\"tiny\":1E-28,\"zero\":0}", "528e9d27a2b9cafbbe249ca361a72fefe6fe062ab1048cc05b44d4d7a2eadfea"],
        ["{\"😀\":\"é😀<>&'\\\"\\\\/\\u0001\\u2028\",\"a\":\"value\",\"A\":\"value\"}", "{\"A\":\"value\",\"a\":\"value\",\"\\uD83D\\uDE00\":\"\\u00E9\\uD83D\\uDE00\\u003C\\u003E\\u0026\\u0027\\u0022\\\\/\\u0001\\u2028\"}", "efddeb97585d0c34157e51629ee165bf73714c8fbf077064b0b764457ce46ec1"],
    ];

    public static IEnumerable<object[]> NumberVectors =>
    [
        ["0.1234567890123456789012345678", "0.1234567890123456789012345678"],
        ["79228162514264337593543950335", "79228162514264337593543950335"],
        ["1e-28", "1E-28"],
        ["1e+28", "10000000000000000000000000000"],
        ["1.2300e+3", "1230"],
        ["-0.0", "0"],
        ["0e+999999", "0"],
    ];

    [Theory]
    [MemberData(nameof(NumberVectors))]
    public void Canonicalize_uses_portable_exact_number_vectors(string input, string expected)
    {
        using var document = JsonDocument.Parse($"{{\"value\":{input}}}");

        var actual = Encoding.UTF8.GetString(CanonicalJson.Canonicalize(document.RootElement));

        actual.Should().Be($"{{\"value\":{expected}}}");
    }

    [Theory]
    [MemberData(nameof(CrossLanguageGoldenVectors))]
    public void Canonicalize_matches_independent_python_bytes_and_digests(
        string input,
        string expected,
        string expectedDigest)
    {
        using var document = JsonDocument.Parse(input);

        var actual = CanonicalJson.Canonicalize(document.RootElement);

        Encoding.UTF8.GetString(actual).Should().Be(expected);
        Convert.ToHexString(SHA256.HashData(actual)).ToLowerInvariant().Should().Be(expectedDigest);
    }

    [Theory]
    [InlineData("1e-29")]
    [InlineData("1e+29")]
    [InlineData("0.12345678901234567890123456789")]
    [InlineData("0.123456789012345678901234567890")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("1e+999")]
    [InlineData("1e-999")]
    public void Canonicalize_rejects_numbers_that_would_require_lossy_fallback(string input)
    {
        using var document = JsonDocument.Parse($"{{\"value\":{input}}}");

        var act = () => CanonicalJson.Canonicalize(document.RootElement);

        act.Should().Throw<JsonException>().WithMessage("*precision loss*");
    }

    [Fact]
    public void Canonicalize_matches_portable_unicode_escaping_and_ordinal_order()
    {
        using var document = JsonDocument.Parse(
            "{\"😀\":\"é😀<>&'\\\"\\\\/\\u0001\\u2028\",\"a\":\"value\",\"A\":\"value\"}");

        var actual = Encoding.UTF8.GetString(CanonicalJson.Canonicalize(document.RootElement));

        actual.Should().Be("{\"A\":\"value\",\"a\":\"value\",\"\\uD83D\\uDE00\":\"\\u00E9\\uD83D\\uDE00\\u003C\\u003E\\u0026\\u0027\\u0022\\\\/\\u0001\\u2028\"}");
    }
}
