using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class IncidentQueryTests
{
    [Fact]
    public void NormalizeTrimsSearchAndPreservesFilters()
    {
        var query = new IncidentQuery("  timeout  ", IncidentSeverity.Critical, true).Normalize();
        Assert.Equal("timeout", query.Search);
        Assert.Equal(IncidentSeverity.Critical, query.Severity);
        Assert.True(query.OpenOnly);
    }

    [Fact]
    public void NormalizeRejectsOversizedSearch() =>
        Assert.Throws<ArgumentException>(() => new IncidentQuery(new string('x', 121)).Normalize());

    [Fact]
    public void SnapshotDerivesAcknowledgedState()
    {
        var open = Incident(DateTimeOffset.UtcNow, null);
        var acknowledged = Incident(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.False(open.IsAcknowledged);
        Assert.True(acknowledged.IsAcknowledged);
    }

    private static OperationalIncidentSnapshot Incident(DateTimeOffset detected, DateTimeOffset? acknowledged) =>
        new("iris:key", IncidentSource.Iris, IncidentSeverity.Critical, "TIMEOUT",
            "Falha", "Detalhe", "12345678", detected, acknowledged);
}
