using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using YARG.Online.Game.Auth;

namespace YARG.Online.Game.Tests.Auth;

public class GameAuthOptionsValidationTests
{
    private static IHost BuildHost(IReadOnlyDictionary<string, string?> gameAuthValues)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(gameAuthValues);

        builder.Services.AddOptions<GameAuthOptions>()
            .Bind(builder.Configuration.GetSection(GameAuthOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer), "GameAuth:Issuer is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Audience), "GameAuth:Audience is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.SigningSecret), "GameAuth:SigningSecret is required.")
            .Validate(
                o => string.IsNullOrEmpty(o.SigningSecret) || Encoding.UTF8.GetByteCount(o.SigningSecret) >= 32,
                "GameAuth:SigningSecret must be at least 32 UTF-8 bytes for HS256.")
            .ValidateOnStart();

        return builder.Build();
    }

    private static async Task AssertValidationFails(IReadOnlyDictionary<string, string?> values, string expectedFragment)
    {
        using var host = BuildHost(values);
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(expectedFragment, string.Join(" | ", ex.Failures));
    }

    [Fact]
    public Task Missing_issuer_fails_validation() => AssertValidationFails(
        new Dictionary<string, string?>
        {
            ["GameAuth:Issuer"] = "",
            ["GameAuth:Audience"] = "yarg-game",
            ["GameAuth:SigningSecret"] = "test-secret-must-be-32-bytes-or-more-please",
        },
        "GameAuth:Issuer is required.");

    [Fact]
    public Task Missing_audience_fails_validation() => AssertValidationFails(
        new Dictionary<string, string?>
        {
            ["GameAuth:Issuer"] = "yarg-server-browser",
            ["GameAuth:Audience"] = "",
            ["GameAuth:SigningSecret"] = "test-secret-must-be-32-bytes-or-more-please",
        },
        "GameAuth:Audience is required.");

    [Fact]
    public Task Missing_signing_secret_fails_validation() => AssertValidationFails(
        new Dictionary<string, string?>
        {
            ["GameAuth:Issuer"] = "yarg-server-browser",
            ["GameAuth:Audience"] = "yarg-game",
            ["GameAuth:SigningSecret"] = null,
        },
        "GameAuth:SigningSecret is required.");

    [Fact]
    public Task Too_short_signing_secret_fails_validation() => AssertValidationFails(
        new Dictionary<string, string?>
        {
            ["GameAuth:Issuer"] = "yarg-server-browser",
            ["GameAuth:Audience"] = "yarg-game",
            ["GameAuth:SigningSecret"] = "short",
        },
        "at least 32 UTF-8 bytes");

    [Fact]
    public async Task Fully_configured_options_pass_validation()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["GameAuth:Issuer"] = "yarg-server-browser",
            ["GameAuth:Audience"] = "yarg-game",
            ["GameAuth:SigningSecret"] = "test-secret-must-be-32-bytes-or-more-please",
        });

        await host.StartAsync();

        var opts = host.Services.GetRequiredService<IOptions<GameAuthOptions>>().Value;
        Assert.Equal("yarg-server-browser", opts.Issuer);
        Assert.Equal("yarg-game", opts.Audience);

        await host.StopAsync();
    }
}
