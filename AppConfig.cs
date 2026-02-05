using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace RoundSoundMimic;

public sealed class AppConfig
{
    public string ServerUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    public static string GetConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "RoundSoundMimic");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "config.json");
    }

    public static async Task<AppConfig> LoadAsync()
    {
        var path = GetConfigPath();
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
    }

    public async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(GetConfigPath(), json).ConfigureAwait(false);
    }
}
