using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Copilotd.Infrastructure;

/// <summary>
/// Manages the Copilot CLI user settings file. Copilot CLI 1.0.103+ retired the
/// <c>--remote</c> startup flag and enables remote control through the
/// <c>remoteSessions</c> setting, so copilotd ensures that setting is enabled
/// before launching remote-enabled sessions.
/// </summary>
public sealed class CopilotSettingsService
{
    public const string RemoteSessionsSetting = "remoteSessions";
    public const string CopilotHomeEnvironmentVariable = "COPILOT_HOME";

    private readonly ILogger<CopilotSettingsService> _logger;

    public CopilotSettingsService(ILogger<CopilotSettingsService> logger)
    {
        _logger = logger;
        SettingsPath = ResolveSettingsPath();
    }

    public string SettingsPath { get; }

    /// <summary>
    /// Ensures <c>remoteSessions</c> is true in the Copilot CLI settings file.
    /// Returns false when the file exists but cannot be read or written safely.
    /// </summary>
    public bool EnsureRemoteSessionsEnabled()
    {
        try
        {
            var root = ReadSettings();
            var existing = root[RemoteSessionsSetting];
            if (existing is JsonValue existingValue
                && existingValue.TryGetValue<bool>(out var enabled)
                && enabled)
            {
                return true;
            }

            root[RemoteSessionsSetting] = true;
            WriteSettings(root);
            _logger.LogInformation(
                "Enabled remote Copilot sessions in {Path} ({Setting}=true)",
                SettingsPath,
                RemoteSessionsSetting);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not enable {Setting} in Copilot settings at {Path}; remote control may be unavailable",
                RemoteSessionsSetting,
                SettingsPath);
            return false;
        }
    }

    private JsonObject ReadSettings()
    {
        if (!File.Exists(SettingsPath))
            return new JsonObject();

        var json = File.ReadAllText(SettingsPath);
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();

        var node = JsonNode.Parse(json, nodeOptions: null, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        return node as JsonObject
               ?? throw new JsonException("Copilot settings file is not a JSON object.");
    }

    private void WriteSettings(JsonObject root)
    {
        var directory = Path.GetDirectoryName(SettingsPath)
                        ?? throw new InvalidOperationException("Copilot settings path has no directory.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static string ResolveSettingsPath()
    {
        var copilotHome = Environment.GetEnvironmentVariable(CopilotHomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(copilotHome))
        {
            var expanded = CopilotdPaths.ExpandUserProfile(copilotHome.Trim());
            return Path.Combine(Path.GetFullPath(expanded), "settings.json");
        }

        return Path.Combine(CopilotdPaths.GetCopilotHomeDirectory(), "settings.json");
    }
}
