namespace AgentFleet.Tests;

/// <summary>
/// The three edit check settings as the configuration gives them. The defaults are part of the behaviour: with no
/// configuration the check is on, with a 20 second limit and a patience of 3.
/// </summary>
public sealed class EditCheckSettingsTests
{
    [Fact]
    public void With_no_configuration_the_check_is_on_with_twenty_seconds_and_patience_three()
    {
        EditCheckSettings settings = EditCheckSettings.Parse(null, null, null);

        Assert.True(settings.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(20), settings.MaximumCheckTime);
        Assert.Equal(3, settings.Patience);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("Off")]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    [InlineData("0")]
    [InlineData(" off ")]
    [InlineData(" 0 ")]
    [InlineData("\tfalse\t")]
    public void Each_off_spelling_switches_the_check_off(string value)
    {
        Assert.False(EditCheckSettings.Parse(value, null, null).Enabled);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("ON")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("offish")]
    [InlineData("1")]
    public void On_and_anything_else_leave_the_check_on(string value)
    {
        Assert.True(EditCheckSettings.Parse(value, null, null).Enabled);
    }

    [Theory]
    [InlineData("4", 20)]
    [InlineData("5", 5)]
    [InlineData("120", 120)]
    [InlineData("121", 20)]
    [InlineData("0", 20)]
    [InlineData("20", 20)]
    [InlineData(" 30 ", 30)]
    public void Seconds_are_accepted_from_five_to_one_hundred_and_twenty_and_otherwise_default(string value, int expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected), EditCheckSettings.Parse(null, value, null).MaximumCheckTime);
    }

    [Theory]
    [InlineData("0", 3)]
    [InlineData("1", 1)]
    [InlineData("10", 10)]
    [InlineData("11", 3)]
    [InlineData("-1", 3)]
    [InlineData("5", 5)]
    public void Patience_is_accepted_from_one_to_ten_and_otherwise_defaults(string value, int expected)
    {
        Assert.Equal(expected, EditCheckSettings.Parse(null, null, value).Patience);
    }

    [Fact]
    public void Non_numeric_seconds_and_patience_take_the_defaults()
    {
        EditCheckSettings settings = EditCheckSettings.Parse("on", "abc", "two");

        Assert.True(settings.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(20), settings.MaximumCheckTime);
        Assert.Equal(3, settings.Patience);
    }

    [Fact]
    public void The_three_values_are_read_independently()
    {
        EditCheckSettings settings = EditCheckSettings.Parse("off", "45", "7");

        Assert.False(settings.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(45), settings.MaximumCheckTime);
        Assert.Equal(7, settings.Patience);
    }
}
