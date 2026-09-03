using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class FarmScheduleTests
{
    [Fact]
    public void RequestValidatesNameCatalogIntervalAndFirstOccurrence()
    {
        var now = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var valid = new FarmScheduleRequest(
            "  verificação horária  ",
            FarmWorkloadCatalog.DefaultId,
            60).Validate(now);

        Assert.Equal("verificação horária", valid.Name);
        Assert.Equal(now, valid.FirstRunAtUtc);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FarmScheduleRequest("inválido", FarmWorkloadCatalog.DefaultId, 4).Validate(now));
        Assert.Throws<ArgumentException>(() =>
            new FarmScheduleRequest("inválido", "shell-arbitrário", 60).Validate(now));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FarmScheduleRequest("inválido", FarmWorkloadCatalog.DefaultId, 60, now.AddDays(31)).Validate(now));
    }

    [Fact]
    public void RecurrenceSkipsMissedWindowsWithoutCausingBurst()
    {
        var occurrence = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            occurrence.AddMinutes(15),
            FarmScheduleRecurrence.Next(occurrence, 15, occurrence.AddMinutes(1)));
        Assert.Equal(
            new DateTimeOffset(2026, 9, 2, 11, 15, 0, TimeSpan.Zero),
            FarmScheduleRecurrence.Next(occurrence, 15, occurrence.AddMinutes(70)));
    }
}
