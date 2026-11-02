namespace Copilotd.Infrastructure;

/// <summary>
/// Minimal <c>.env</c> reader used for provider configuration. Supports comments,
/// an optional <c>export</c> prefix, and single/double quoted values. Values are
/// returned as-is (no variable expansion) and never written to the daemon process
/// environment; callers apply them only to launched Copilot processes.
/// </summary>
public static class DotEnvLoader
{
    public const string EnvFileOverrideVariable = "COPILOTD_ENV_FILE";

    /// <summary>
    /// Loads provider-related values from, in order:
    /// <c>COPILOTD_ENV_FILE</c>, <c>&lt;cwd&gt;/.env</c>, then <c>&lt;copilotd home&gt;/.env</c>.
    /// Earlier files win; later files fill in missing keys.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Load()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in EnumerateCandidatePaths())
        {
            if (path is null || !File.Exists(path))
                continue;

            foreach (var (key, value) in ReadFile(path))
            {
                if (!values.ContainsKey(key))
                    values[key] = value;
            }
        }

        return values;
    }

    private static IEnumerable<string?> EnumerateCandidatePaths()
    {
        var overridePath = Environment.GetEnvironmentVariable(EnvFileOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            yield return CopilotdPaths.ExpandUserProfile(overridePath.Trim());
        }

        yield return Path.Combine(Directory.GetCurrentDirectory(), ".env");
        yield return CopilotdPaths.GetEnvFilePath();
    }

    private static IEnumerable<KeyValuePair<string, string>> ReadFile(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                line = line["export ".Length..].TrimStart();

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = line[..separatorIndex].Trim();
            if (key.Length == 0 || key.Contains(' '))
                continue;

            var value = line[(separatorIndex + 1)..].Trim();
            value = Unquote(value);
            yield return new KeyValuePair<string, string>(key, value);
        }
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1]
                .Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\r", "\r", StringComparison.Ordinal)
                .Replace("\\t", "\t", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1];

        return value;
    }
}
