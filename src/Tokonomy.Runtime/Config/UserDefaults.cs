using System.Text.Json;

namespace Tokonomy.Runtime.Config;

/// <summary>
/// What you picked last time, kept in your user profile (never in a project, never a key), so the next project starts with
/// one question instead of the whole setup.
/// </summary>
public sealed class UserDefaults
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary><c>claude</c>, <c>antigravity</c> or an API service id (<c>openrouter</c>, <c>anthropic</c>, <c>gemini</c>, <c>openai</c>, <c>deepseek</c>).</summary>
    public string Strong { get; set; } = "";

    public string Cheap { get; set; } = "";
    public string ArchitectModel { get; set; } = "";
    public string WorkerModel { get; set; } = "";

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tokonomy", "defaults.json");

    public bool IsComplete => ArchitectModel.Length > 0 && WorkerModel.Length > 0;

    public static string Id(Connection connection) => connection.Kind switch
    {
        ConnectionKind.ClaudeCode => "claude",
        ConnectionKind.Antigravity => "antigravity",
        _ => connection.Api!.Id,
    };

    public static UserDefaults? Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            return File.Exists(path) ? JsonSerializer.Deserialize<UserDefaults>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Best effort: a read-only profile must never stop the setup.</summary>
    public static void Save(SetupChoices choices, string? path = null)
    {
        // An unfinished setup (a model still to be chosen) must not become what the next project is offered.
        if (choices.ArchitectModel.Length == 0 || choices.WorkerModel.Length == 0)
        {
            return;
        }

        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new UserDefaults
            {
                Strong = Id(choices.Strong),
                Cheap = Id(choices.Cheap),
                ArchitectModel = choices.ArchitectModel,
                WorkerModel = choices.WorkerModel,
            }, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
