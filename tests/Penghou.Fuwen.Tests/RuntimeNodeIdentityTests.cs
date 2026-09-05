using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class RuntimeNodeIdentityTests
{
    [Fact]
    public void Fan_out_identity_is_stable_and_type_sensitive()
    {
        var path = StructuralNodeIdentity.Create("video", "scenes");

        var first = RuntimeNodeIdentity.CreateFanOutItem(path, new StringRuntimeKey("1"), "render");
        var repeated = RuntimeNodeIdentity.CreateFanOutItem(path, new StringRuntimeKey("1"), "render");
        var integer = RuntimeNodeIdentity.CreateFanOutItem(path, new IntegerRuntimeKey(1), "render");

        first.Should().Be(repeated).And.NotBe(integer);
        first.Should().StartWith("video/scenes/$item/sha256-").And.EndWith("/render");
    }

    [Fact]
    public void Duplicate_keys_are_rejected_before_fan_out()
    {
        var keys = new RuntimeIdentityKey[]
        {
            new StringRuntimeKey("scene-a"),
            new StringRuntimeKey("scene-a"),
        };

        var act = () => RuntimeNodeIdentity.ValidateUniqueKeys(keys);

        act.Should().Throw<ArgumentException>().WithMessage("*must be unique*");
    }

    [Fact]
    public void Positional_identity_is_an_explicit_distinct_key_kind()
    {
        var positional = RuntimeNodeIdentity.GetKeyPathSegment(new PositionalRuntimeKey(0));
        var domainInteger = RuntimeNodeIdentity.GetKeyPathSegment(new IntegerRuntimeKey(0));

        positional.Should().NotBe(domainInteger);
    }

    [Fact]
    public void Uuid_keys_require_canonical_lowercase_D_format()
    {
        var value = Guid.NewGuid().ToString("D").ToUpperInvariant();

        var act = () => RuntimeNodeIdentity.GetCanonicalKeyBytes(new UuidRuntimeKey(value));

        act.Should().Throw<ArgumentException>().WithMessage("*lowercase canonical D format*");
    }

    [Fact]
    public void Iteration_identity_uses_intrinsic_zero_based_sequence()
    {
        var path = RuntimeNodeIdentity.CreateIteration("repair/repair_loop", 2, "apply");

        path.Should().Be("repair/repair_loop/$iteration/2/apply");
    }
}
