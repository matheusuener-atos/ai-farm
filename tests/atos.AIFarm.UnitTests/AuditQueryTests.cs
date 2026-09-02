using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class AuditQueryTests
{
    [Fact]
    public void NormalizeTrimsSearchAndConvertsPeriodToUtc()
    {
        var query = new AuditQuery(
            "  completed  ",
            AuditCategory.Farm,
            new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.FromHours(-3)),
            new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.FromHours(-3)),
            2,
            50).Normalize();

        Assert.Equal("completed", query.Search);
        Assert.Equal(TimeSpan.Zero, query.FromUtc?.Offset);
        Assert.Equal(13, query.FromUtc?.Hour);
        Assert.Equal(2, query.Page);
        Assert.Equal(50, query.PageSize);
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void NormalizeRejectsInvalidPagination(int page, int pageSize) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AuditQuery(Page: page, PageSize: pageSize).Normalize());

    [Fact]
    public void NormalizeRejectsInvertedPeriod() =>
        Assert.Throws<ArgumentException>(() => new AuditQuery(
            FromUtc: DateTimeOffset.UtcNow,
            ToUtc: DateTimeOffset.UtcNow.AddDays(-1)).Normalize());

    [Fact]
    public void PageReportsNavigationFromFilteredTotal()
    {
        var page = new AuditPage(
            [], new AuditTotals(51, 10, 10, 10, 10, 11, 0, 0), 2, 25, false);

        Assert.Equal(3, page.TotalPages);
        Assert.True(page.HasPreviousPage);
        Assert.True(page.HasNextPage);
    }
}
