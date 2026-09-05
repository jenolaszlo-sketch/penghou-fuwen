using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class CanonicalJsonTests
{
    [Fact]
    public void Canonicalize_orders_properties_and_normalizes_numbers()
    {
        using var document = JsonDocument.Parse("""{"z":1.2300,"a":{"y":-0.0,"x":"é"},"items":[3,2,1]}""");

        var actual = Encoding.UTF8.GetString(CanonicalJson.Canonicalize(document.RootElement));

        actual.Should().Be("""{"a":{"x":"\u00E9","y":0},"items":[3,2,1],"z":1.23}""");
    }

    [Fact]
    public void Canonicalize_rejects_duplicate_object_properties()
    {
        using var document = JsonDocument.Parse("""{"a":1,"a":2}""");

        var act = () => CanonicalJson.Canonicalize(document.RootElement);

        act.Should().Throw<JsonException>().WithMessage("*Duplicate JSON property 'a'*");
    }
}
