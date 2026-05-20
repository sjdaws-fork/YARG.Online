using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using YARG.Online.Game.Auth;

namespace YARG.Online.Game.Tests.Auth;

public class GameJwtValidatorTests
{
    private static GameJwtValidator BuildValidator(GameAuthOptions? overrides = null)
    {
        var opts = overrides ?? new GameAuthOptions
        {
            Issuer = TestJwt.DefaultIssuer,
            Audience = TestJwt.DefaultAudience,
            SigningSecret = TestJwt.DefaultSecret,
        };
        return new GameJwtValidator(Options.Create(opts), NullLogger<GameJwtValidator>.Instance);
    }

    [Fact]
    public void Valid_token_returns_user_id_and_display_name()
    {
        var validator = BuildValidator();
        var token = TestJwt.Mint(userId: "u_alice", displayName: "Alice", lobbyId: "lob_abc", expectedMembers: 3);

        var result = validator.Validate(token);

        Assert.True(result.IsValid);
        Assert.Equal("u_alice", result.UserId);
        Assert.Equal("Alice", result.DisplayName);
        Assert.Equal("lob_abc", result.LobbyId);
        Assert.Equal(3, result.ExpectedMembers);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void Token_with_wrong_audience_is_rejected()
    {
        var validator = BuildValidator();
        var token = TestJwt.Mint(audience: "yarg-api");

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void Token_with_wrong_issuer_is_rejected()
    {
        var validator = BuildValidator();
        var token = TestJwt.Mint(issuer: "some-other-issuer");

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var validator = BuildValidator();
        var token = TestJwt.Mint(
            now: DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
            lifetime: TimeSpan.FromMinutes(5));

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Token_signed_with_different_secret_is_rejected()
    {
        var validator = BuildValidator();
        var token = TestJwt.Mint(secret: "a-completely-different-secret-32-bytes!!");

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Empty_token_is_rejected_without_crashing()
    {
        var validator = BuildValidator();

        var result = validator.Validate(string.Empty);

        Assert.False(result.IsValid);
        Assert.Equal("empty token", result.FailureReason);
    }

    [Fact]
    public void Malformed_token_is_rejected_without_crashing()
    {
        var validator = BuildValidator();

        var result = validator.Validate("this.is.not.a.jwt");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Token_without_sub_claim_is_rejected()
    {
        var validator = BuildValidator();
        // Mint a token but strip the sub by re-using a custom claims dict path: we just override sub to empty.
        var token = TestJwt.Mint(userId: "", displayName: "Anon");

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
        Assert.Contains("sub", result.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Display_name_falls_back_to_user_id_when_name_claim_missing()
    {
        var validator = BuildValidator();
        // Empty display name -> name claim is empty -> falls back to sub.
        var token = TestJwt.Mint(userId: "u_bob", displayName: "");

        var result = validator.Validate(token);

        Assert.True(result.IsValid);
        Assert.Equal("u_bob", result.UserId);
        Assert.Equal("u_bob", result.DisplayName);
    }

    [Fact]
    public void Token_without_lobby_id_is_rejected()
    {
        var validator = BuildValidator();
        var token = TestJwt.Mint(lobbyId: "");

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
        Assert.Contains("lobby_id", result.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Token_with_zero_expected_members_is_rejected()
    {
        var validator = BuildValidator();
        var token = TestJwt.Mint(expectedMembers: 0);

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
        Assert.Contains("expected_members", result.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
