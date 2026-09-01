using System.Text.Json;

namespace atos.AIFarm.Agent;

internal sealed class AgentConfigurationStore(string? dataDirectory = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string DirectoryPath { get; } = dataDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "atos-ai-farm");

    public string ConfigurationPath => Path.Combine(DirectoryPath, "agent.json");

    public async Task SaveAsync(
        AgentConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = Path.Combine(DirectoryPath, $"agent-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(configuration, JsonOptions),
            cancellationToken);
        File.Move(temporary, ConfigurationPath, true);
    }

    public async Task<AgentConfiguration?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigurationPath)) return null;
        await using var stream = File.OpenRead(ConfigurationPath);
        return await JsonSerializer.DeserializeAsync<AgentConfiguration>(
            stream,
            JsonOptions,
            cancellationToken);
    }
}
