using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class ConversationAiSettingsTests
{
    [Fact]
    public void NormalizeTrimsValuesAndRoundsTemperature()
    {
        var settings = new ConversationAiSettings(
            "  model-v1  ",
            "  Responda em português.  ",
            0.876m,
            1_024).Normalize();

        Assert.Equal("model-v1", settings.ModelPreference);
        Assert.Equal("Responda em português.", settings.SystemInstruction);
        Assert.Equal(0.88m, settings.Temperature);
        Assert.Equal(1_024, settings.MaximumOutputTokens);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(2.1)]
    public void NormalizeRejectsTemperatureOutsideResponsesApiRange(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (ConversationAiSettings.Default with { Temperature = (decimal)value }).Normalize());
    }

    [Theory]
    [InlineData(63)]
    [InlineData(128001)]
    public void NormalizeRejectsUnsafeOutputLimit(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (ConversationAiSettings.Default with { MaximumOutputTokens = value }).Normalize());
    }
}
