using KeedParsecNative.Core.Session;
using Xunit;

namespace KeedParsecNative.UnitTests;

public class RoomCodeGeneratorTests
{
    [Fact]
    public void Generate_ProducesExpectedLength()
    {
        var code = RoomCodeGenerator.Generate(6);
        Assert.Equal(6, code.Length);
    }

    [Fact]
    public void Generate_UsesUnambiguousAlphabet()
    {
        for (var i = 0; i < 100; i++)
        {
            var code = RoomCodeGenerator.Generate(6);
            Assert.DoesNotContain('0', code);
            Assert.DoesNotContain('O', code);
            Assert.DoesNotContain('1', code);
            Assert.DoesNotContain('I', code);
            Assert.DoesNotContain('L', code);
        }
    }

    [Fact]
    public void Generate_CodesAreUnique()
    {
        var codes = Enumerable.Range(0, 100).Select(_ => RoomCodeGenerator.Generate()).ToHashSet();
        Assert.Equal(100, codes.Count);
    }

    [Theory]
    [InlineData("ABC234", true)]
    [InlineData("abc234", true)]
    [InlineData("ABC23", false)]
    [InlineData("ABC23!", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidFormat_Validates(string? code, bool expected)
    {
        Assert.Equal(expected, RoomCodeGenerator.IsValidFormat(code));
    }
}
