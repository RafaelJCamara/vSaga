using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Identity.Tests;

public sealed partial class FirstAdministratorSettingsTests
{
    [Fact]
    public void Read_WithNothingSet_SeedsNothing_AndLeavesTheCodeToBeGenerated()
    {
        var settings = FirstAdministratorSettings.Read(Configuration());

        Assert.False(settings.SeedConfigured);
        Assert.False(settings.ResetOnStart);
        Assert.Null(settings.SetupCode);
    }

    [Theory]
    [InlineData("admin", null)]
    [InlineData(null, "a password")]
    [InlineData(" admin ", "a password")]
    public void Read_EitherSeedKey_CountsAsASeed(string? username, string? password)
    {
        var settings = FirstAdministratorSettings.Read(Configuration(
            (FirstAdministratorSettings.UsernameKey, username), (FirstAdministratorSettings.PasswordKey, password)));

        Assert.True(settings.SeedConfigured);
        Assert.Equal(username?.Trim(), settings.Username);
        Assert.Equal(password, settings.Password);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    public void Read_ResetOnStartThatIsNotABoolean_FailsNamingTheKey(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => FirstAdministratorSettings.Read(Configuration(
            (FirstAdministratorSettings.UsernameKey, "admin"), (FirstAdministratorSettings.ResetOnStartKey, value))));

        Assert.Contains(FirstAdministratorSettings.ResetOnStartKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ResetOnStartWithNoSeedKey_Fails()
    {
        var error = Assert.Throws<InvalidOperationException>(() => FirstAdministratorSettings.Read(Configuration(
            (FirstAdministratorSettings.ResetOnStartKey, "true"))));

        Assert.Contains(FirstAdministratorSettings.UsernameKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ABCD-EFGH-JKLM-NPQ")]
    [InlineData("abcd efgh jkl -- mnpq")]
    public void Read_APresetCodeShorterThanAGeneratedOne_Fails(string code)
    {
        var error = Assert.Throws<InvalidOperationException>(() => FirstAdministratorSettings.Read(Configuration(
            (FirstAdministratorSettings.SetupCodeKey, code))));

        Assert.Contains(FirstAdministratorSettings.SetupCodeKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_APresetCodeOfSixteenCharacters_IsKept()
    {
        var settings = FirstAdministratorSettings.Read(Configuration((FirstAdministratorSettings.SetupCodeKey, " abcd-efgh-jkmn-pqrs ")));

        Assert.Equal("abcd-efgh-jkmn-pqrs", settings.SetupCode);
    }

    [Fact]
    public void Generate_IsFourGroupsOfFourWithoutLookAlikes_AndDifferentEachTime()
    {
        var codes = Enumerable.Range(0, 50).Select(_ => SetupCodes.Generate()).ToList();

        Assert.All(codes, code => Assert.Matches(CodeShape(), code));
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("K7QD-M2XH-9TPA-W4RC", true)]
    [InlineData("k7qd m2xh 9tpa w4rc", true)]
    [InlineData("K7QDM2XH9TPAW4RC", true)]
    [InlineData("K7QD-M2XH-9TPA-W4RD", false)]
    [InlineData("K7QD-M2XH-9TPA", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Matches_IgnoresCaseSpacesAndHyphens_AndNothingElse(string? submitted, bool expected) =>
        Assert.Equal(expected, SetupCodes.Matches("K7QD-M2XH-9TPA-W4RC", submitted));

    [Fact]
    public void FirstRunState_IsClosedUntilStartUpOpensIt_AndMatchesNothing()
    {
        var state = new FirstRunState();
        Assert.False(state.IsSetupOpen);
        Assert.False(state.MatchesSetupCode("ANYTHING"));
        Assert.Equal("No setup code is in force; restart the API and it logs a new one.", state.SetupUnavailableReason);
    }

    [GeneratedRegex("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CodeShape();

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal))
            .Build();
}
