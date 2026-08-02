using FluentAssertions;
using MeetingRecorder.Infrastructure.Security;
using Microsoft.Extensions.Options;
using System.IdentityModel.Tokens.Jwt;
namespace MeetingRecorder.UnitTests;

public class JwtTokenServiceTests
{
    private static readonly JwtOptions JwtSettings = new()
    {
        Issuer = "TestIssuer",
        Audience = "TestAudience",
        Key = "test-signing-key-that-is-long-enough-1234567890",
        ExpiryMinutes = 60
    };

    private JwtTokenService CreateSut() => new(Options.Create(JwtSettings));

    [Fact]
    public void GenerateToken_ProducesValidTokenWithClaims()
    {
        var sut = CreateSut();
        var userId = Guid.NewGuid();

        var (token, expiresAt) = sut.GenerateToken(userId, "u@test.com", "Test User", "Admin");

        token.Should().NotBeNullOrWhiteSpace();
        expiresAt.Should().BeAfter(DateTime.UtcNow);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        jwt.Claims.Should().Contain(c => c.Type == "sub" && c.Value == userId.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "email" && c.Value == "u@test.com");
        // Role is stored as ClaimTypes.Role (long-form URI) so TokenValidationParameters
        // with RoleClaimType = ClaimTypes.Role maps it back to IsInRole correctly.
        jwt.Claims.Should().Contain(c => c.Type == System.Security.Claims.ClaimTypes.Role && c.Value == "Admin");
    }

    [Fact]
    public void ValidateToken_WithValidToken_ReturnsPrincipal()
    {
        var sut = CreateSut();
        var (token, _) = sut.GenerateToken(Guid.NewGuid(), "u@test.com", "N", "User");

        var principal = sut.ValidateToken(token);

        principal.Should().NotBeNull();
        principal!.Identity!.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public void ValidateToken_WithTamperedToken_ReturnsNull()
    {
        var sut = CreateSut();
        var (token, _) = sut.GenerateToken(Guid.NewGuid(), "u@test.com", "N", "User");

        var tampered = token[..^4] + (token[^4] == 'a' ? "bbbb" : "aaaa");
        var result = sut.ValidateToken(tampered);

        result.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_WithWrongKey_ReturnsNull()
    {
        var sut = CreateSut();
        var (token, _) = sut.GenerateToken(Guid.NewGuid(), "u@test.com", "N", "User");

        var otherSut = new JwtTokenService(Options.Create(new JwtOptions
        {
            Issuer = JwtSettings.Issuer,
            Audience = JwtSettings.Audience,
            Key = "a-different-signing-key-1234567890-extra-long",
            ExpiryMinutes = 60
        }));

        otherSut.ValidateToken(token).Should().BeNull();
    }
}
