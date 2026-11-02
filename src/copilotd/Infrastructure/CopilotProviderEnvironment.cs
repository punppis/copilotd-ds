using Copilotd.Models;

namespace Copilotd.Infrastructure;

/// <summary>
/// Effective provider settings after merging process environment, <c>.env</c>, and config.
/// </summary>
public sealed record ResolvedCopilotProvider(
    bool IsActive,
    string Type,
    string BaseUrl,
    string? Model,
    string? ApiKey,
    string? ApiKeySource,
    int? MaxPromptTokens,
    int? MaxOutputTokens);

/// <summary>
/// Resolves DeepSeek/BYOK provider settings and builds the <c>COPILOT_PROVIDER_*</c>
/// environment overrides applied to every Copilot CLI process launched by copilotd.
///
/// Precedence for provider values: process environment, then <c>.env</c>, then config.json.
/// BYOK only activates when an API key is available; otherwise copilotd uses the
/// GitHub-hosted DeepSeek catalog model.
/// </summary>
public static class CopilotProviderEnvironment
{
    public const string ProviderTypeVariable = "COPILOT_PROVIDER_TYPE";
    public const string ProviderBaseUrlVariable = "COPILOT_PROVIDER_BASE_URL";
    public const string ProviderApiKeyVariable = "COPILOT_PROVIDER_API_KEY";
    public const string ModelVariable = "COPILOT_MODEL";
    public const string MaxPromptTokensVariable = "COPILOT_PROVIDER_MAX_PROMPT_TOKENS";
    public const string MaxOutputTokensVariable = "COPILOT_PROVIDER_MAX_OUTPUT_TOKENS";
    public const string DeepSeekApiKeyVariable = "DEEPSEEK_API_KEY";

    public static ResolvedCopilotProvider Resolve(CopilotdConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var provider = config.Provider ?? new CopilotProviderConfig();
        var dotEnv = DotEnvLoader.Load();
        var type = FirstNonEmpty(
            Environment.GetEnvironmentVariable(ProviderTypeVariable),
            dotEnv.GetValueOrDefault(ProviderTypeVariable),
            provider.Type) ?? CopilotProviderConfig.DefaultType;
        var baseUrl = FirstNonEmpty(
            Environment.GetEnvironmentVariable(ProviderBaseUrlVariable),
            dotEnv.GetValueOrDefault(ProviderBaseUrlVariable),
            provider.BaseUrl) ?? CopilotProviderConfig.DefaultBaseUrl;
        var model = FirstNonEmpty(
            Environment.GetEnvironmentVariable(ModelVariable),
            dotEnv.GetValueOrDefault(ModelVariable),
            provider.Model);
        var (apiKey, apiKeySource) = ResolveApiKey(provider, dotEnv);
        var maxPromptTokens = ParsePositiveInt(FirstNonEmpty(
            Environment.GetEnvironmentVariable(MaxPromptTokensVariable),
            dotEnv.GetValueOrDefault(MaxPromptTokensVariable))) ?? provider.MaxPromptTokens;
        var maxOutputTokens = ParsePositiveInt(FirstNonEmpty(
            Environment.GetEnvironmentVariable(MaxOutputTokensVariable),
            dotEnv.GetValueOrDefault(MaxOutputTokensVariable))) ?? provider.MaxOutputTokens;

        var isActive = provider.Enabled
            && !string.IsNullOrWhiteSpace(apiKey)
            && !string.IsNullOrWhiteSpace(baseUrl);

        return new ResolvedCopilotProvider(
            isActive,
            type,
            baseUrl,
            model,
            apiKey,
            apiKeySource,
            maxPromptTokens,
            maxOutputTokens);
    }

    /// <summary>
    /// Returns the effective default model: explicit <c>default_model</c> wins, then the
    /// active BYOK model, then the GitHub-hosted DeepSeek catalog model.
    /// Rule-level model overrides still take precedence at the call site.
    /// </summary>
    public static string? ResolveModel(CopilotdConfig config)
        => ResolveModel(config, Resolve(config));

    public static string? ResolveModel(CopilotdConfig config, ResolvedCopilotProvider resolved)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(resolved);

        if (!string.IsNullOrWhiteSpace(config.DefaultModel))
            return config.DefaultModel;

        if (resolved.IsActive && !string.IsNullOrWhiteSpace(resolved.Model))
            return resolved.Model;

        return string.IsNullOrWhiteSpace(config.AgentModel)
            ? CopilotdConfig.DefaultAgentModel
            : config.AgentModel;
    }

    /// <summary>
    /// Builds the environment overrides that activate BYOK for a launched Copilot process.
    /// Returns an empty list when no API key is available.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> BuildOverrides(
        ResolvedCopilotProvider resolved,
        string? model = null)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        if (!resolved.IsActive || string.IsNullOrWhiteSpace(resolved.ApiKey))
            return [];

        var overrides = new List<KeyValuePair<string, string>>
        {
            new(ProviderTypeVariable, resolved.Type),
            new(ProviderBaseUrlVariable, resolved.BaseUrl),
            new(ProviderApiKeyVariable, resolved.ApiKey),
        };

        var effectiveModel = FirstNonEmpty(model, resolved.Model);
        if (!string.IsNullOrWhiteSpace(effectiveModel))
            overrides.Add(new(ModelVariable, effectiveModel));

        if (resolved.MaxPromptTokens is > 0)
            overrides.Add(new(MaxPromptTokensVariable, resolved.MaxPromptTokens.Value.ToString()));

        if (resolved.MaxOutputTokens is > 0)
            overrides.Add(new(MaxOutputTokensVariable, resolved.MaxOutputTokens.Value.ToString()));

        return overrides;
    }

    /// <summary>
    /// Human-readable provider summary for status output. Never includes the API key.
    /// </summary>
    public static string Describe(CopilotdConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var resolved = Resolve(config);
        if (resolved.IsActive)
        {
            var source = string.IsNullOrWhiteSpace(resolved.ApiKeySource) ? "configured" : resolved.ApiKeySource;
            var model = string.IsNullOrWhiteSpace(resolved.Model) ? CopilotProviderConfig.DefaultModel : resolved.Model;
            return $"BYOK {model} ({resolved.Type} @ {resolved.BaseUrl}, key from {source})";
        }

        var catalogModel = string.IsNullOrWhiteSpace(config.AgentModel)
            ? CopilotdConfig.DefaultAgentModel
            : config.AgentModel;
        return resolved.BaseUrl.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            ? $"{catalogModel} (GitHub catalog; set DEEPSEEK_API_KEY in .env to use the direct DeepSeek API)"
            : $"{catalogModel} (GitHub catalog; BYOK inactive: no API key)";
    }

    private static (string? ApiKey, string? Source) ResolveApiKey(
        CopilotProviderConfig provider,
        IReadOnlyDictionary<string, string> dotEnv)
    {
        var configuredVariable = string.IsNullOrWhiteSpace(provider.ApiKeyEnvironmentVariable)
            ? CopilotProviderConfig.DefaultApiKeyEnvironmentVariable
            : provider.ApiKeyEnvironmentVariable;

        var candidates = new List<(string? Value, string Source)>
        {
            (SafeGetEnvironmentVariable(ProviderApiKeyVariable), $"environment {ProviderApiKeyVariable}"),
            (SafeGetEnvironmentVariable(configuredVariable), $"environment {configuredVariable}"),
            (dotEnv.GetValueOrDefault(ProviderApiKeyVariable), $".env:{ProviderApiKeyVariable}"),
            (dotEnv.GetValueOrDefault(configuredVariable), $".env:{configuredVariable}"),
            (provider.ApiKey, "config provider.api_key"),
        };

        foreach (var (value, source) in candidates)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return (value.Trim(), source);
        }

        return (null, null);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static int? ParsePositiveInt(string? value)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;

    private static string? SafeGetEnvironmentVariable(string? name)
        => string.IsNullOrWhiteSpace(name) || name.Contains('=')
            ? null
            : Environment.GetEnvironmentVariable(name);
}
