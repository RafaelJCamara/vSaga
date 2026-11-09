using VSaga.Dashboard.Identity.Model;

namespace VSaga.Dashboard.Identity.Tests.Model;

public sealed class DisplayPreferencesTests
{
    [Fact]
    public void ToJson_WritesOnlyTheMembersThatAreSet_InAFixedOrder()
    {
        Assert.Equal("""{"theme":"dark","density":"compact"}""", new DisplayPreferences("dark", "compact").ToJson());
        Assert.Equal("""{"theme":"system"}""", new DisplayPreferences("system", null).ToJson());
        Assert.Equal("""{"density":"comfortable"}""", new DisplayPreferences(null, "comfortable").ToJson());
        Assert.Null(new DisplayPreferences(null, null).ToJson());
    }

    [Fact]
    public void ToJson_OfAnyOfferedChoice_FitsTheColumn()
    {
        var longest = Enumerable.Max(DisplayPreferences.Themes, theme => theme.Length)
            + Enumerable.Max(DisplayPreferences.Densities, density => density.Length);

        // Two members, their names and the JSON around them, with room to spare for the next preference.
        Assert.True(longest + 40 < DisplayPreferences.MaxJsonLength);
    }

    [Fact]
    public void Parse_RoundTripsWhatToJsonWrote()
    {
        foreach (var theme in DisplayPreferences.Themes)
        {
            foreach (var density in DisplayPreferences.Densities)
            {
                var preferences = new DisplayPreferences(theme, density);

                Assert.Equal(preferences, DisplayPreferences.Parse(preferences.ToJson()));
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"dark\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"fontSize":"large"}""")]
    [InlineData("""{"theme":"sepia","density":"roomy"}""")]
    [InlineData("""{"theme":7,"density":null}""")]
    public void Parse_OfWhatHoldsNothingThisVersionKnows_IsNull(string? json)
    {
        Assert.Null(DisplayPreferences.Parse(json));
    }

    [Fact]
    public void Parse_DropsAnUnknownKeyAndAnUnknownValue_AndKeepsTheRest()
    {
        var parsed = DisplayPreferences.Parse("""{"theme":"dark","density":"spacious","fontSize":"large"}""");

        Assert.Equal(new DisplayPreferences("dark", null), parsed);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("system", "comfortable", true)]
    [InlineData("high-contrast", null, true)]
    [InlineData("sepia", null, false)]
    [InlineData(null, "roomy", false)]
    [InlineData("Dark", null, false)]
    [InlineData("", null, false)]
    public void IsValid_IsTheClosedSetOfValues(string? theme, string? density, bool expected)
    {
        Assert.Equal(expected, new DisplayPreferences(theme, density).IsValid);
    }
}
