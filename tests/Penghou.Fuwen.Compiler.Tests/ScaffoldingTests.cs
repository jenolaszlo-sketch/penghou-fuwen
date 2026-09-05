using FluentAssertions;

namespace Penghou.Fuwen.Compiler.Tests;

public sealed class ScaffoldingTests
{
    [Fact]
    public void CompilerAssembly_HasExpectedIdentity()
    {
        typeof(AssemblyMarker).Assembly.GetName().Name
            .Should().Be("Penghou.Fuwen.Compiler");
    }
}
