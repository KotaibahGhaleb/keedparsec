using KeedParsecNative.Core.Session;
using Xunit;

namespace KeedParsecNative.UnitTests;

public class JoinTokenServiceTests
{
    private static JoinTokenService CreateService() =>
        new(System.Text.Encoding.UTF8.GetBytes("test-secret-key-that-is-long-enough"));

    [Fact]
    public void CreateAndValidate_RoundTrips()
    {
        var service = CreateService();
        var token = service.CreateToken("ABC234", "peer-1", TimeSpan.FromMinutes(1));
        Assert.True(service.ValidateToken("ABC234", token));
    }

    [Fact]
    public void Validate_RejectsWrongRoom()
    {
        var service = CreateService();
        var token = service.CreateToken("ABC234", "peer-1", TimeSpan.FromMinutes(1));
        Assert.False(service.ValidateToken("XYZ789", token));
    }

    [Fact]
    public void Validate_RejectsExpiredToken()
    {
        var service = CreateService();
        var token = service.CreateToken("ABC234", "peer-1", TimeSpan.FromMilliseconds(-1));
        Assert.False(service.ValidateToken("ABC234", token));
    }

    [Fact]
    public void Validate_RejectsTamperedToken()
    {
        var service = CreateService();
        var token = service.CreateToken("ABC234", "peer-1", TimeSpan.FromMinutes(1));
        var tampered = token[..^2] + (token[^2] == 'A' ? "B" : "A") + token[^1];
        Assert.False(service.ValidateToken("ABC234", tampered));
    }

    [Fact]
    public void Validate_RejectsGarbage()
    {
        var service = CreateService();
        Assert.False(service.ValidateToken("ABC234", "not-a-token"));
        Assert.False(service.ValidateToken("ABC234", ""));
        Assert.False(service.ValidateToken("ABC234", "."));
    }
}
