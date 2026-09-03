using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class FarmJobHistoryQueryTests
{
    [Fact]
    public void NormalizesFiltersAndUtcRange()
    {
        var query = new FarmJobHistoryQuery(
            "  SHA256  ",
            FarmWorkloadCatalog.DefaultId,
            FarmJobStatus.Completed,
            FarmJobOrigin.PersonalApi,
            true,
            DateTimeOffset.Parse("2026-09-01T00:00:00-03:00"),
            DateTimeOffset.Parse("2026-09-02T23:59:59-03:00"),
            2,
            50).Normalize();

        Assert.Equal("SHA256", query.Search);
        Assert.Equal(TimeSpan.Zero, query.FromUtc?.Offset);
        Assert.Equal(TimeSpan.Zero, query.ToUtc?.Offset);
        Assert.Equal(2, query.Page);
        Assert.Equal(50, query.PageSize);
    }

    [Fact]
    public void RejectsInvalidPaginationRangeAndWorkload()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FarmJobHistoryQuery(Page: 0).Normalize());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FarmJobHistoryQuery(PageSize: 101).Normalize());
        Assert.Throws<ArgumentException>(() =>
            new FarmJobHistoryQuery(WorkloadId: "workload-arbitrario").Normalize());
        Assert.Throws<ArgumentException>(() =>
            new FarmJobHistoryQuery(
                FromUtc: DateTimeOffset.Parse("2026-09-03T00:00:00Z"),
                ToUtc: DateTimeOffset.Parse("2026-09-02T00:00:00Z")).Normalize());
    }
}
