using Copilotd.Models;

namespace Copilotd.Infrastructure;

/// <summary>
/// Environment-variable overrides applied on top of config.json. Primarily used by the
/// Docker image so container deployments can configure paths and agents without editing
/// the persisted config file.
/// </summary>
public static class CopilotdEnvironment
{
    public const string RepoHomeVariable = "COPILOTD_REPO_HOME";
    public const string EnableAgentsVariable = "COPILOTD_ENABLE_AGENTS";
    public const string AgentCountVariable = "COPILOTD_AGENT_COUNT";
    public const string AgentNameFormatVariable = "COPILOTD_AGENT_NAME_FORMAT";
    public const string AgentModelVariable = "COPILOTD_AGENT_MODEL";
    public const string EnableControlSessionVariable = "COPILOTD_ENABLE_CONTROL_SESSION";
    public const string DefaultModelVariable = "COPILOTD_DEFAULT_MODEL";
    public const string ProviderEnabledVariable = "COPILOTD_PROVIDER_ENABLED";
    public const string ProviderTypeVariable = "COPILOTD_PROVIDER_TYPE";
    public const string ProviderBaseUrlVariable = "COPILOTD_PROVIDER_BASE_URL";
    public const string ProviderModelVariable = "COPILOTD_PROVIDER_MODEL";
    public const string ProviderApiKeyVariable = "COPILOTD_PROVIDER_API_KEY";
    public const string ProviderApiKeyEnvironmentVariable = "COPILOTD_PROVIDER_API_KEY_ENV";

    public static void ApplyConfigOverrides(CopilotdConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var repoHome = GetValue(RepoHomeVariable);
        if (repoHome is not null)
            config.RepoHome = Path.GetFullPath(CopilotdPaths.ExpandUserProfile(repoHome));

        if (TryParseBoolean(GetValue(EnableAgentsVariable), out var enableAgents))
            config.EnableAgents = enableAgents;

        if (int.TryParse(GetValue(AgentCountVariable), out var agentCount))
            config.AgentCount = agentCount;

        var agentNameFormat = GetValue(AgentNameFormatVariable);
        if (agentNameFormat is not null)
            config.AgentNameFormat = agentNameFormat;

        var agentModel = GetValue(AgentModelVariable);
        if (agentModel is not null)
            config.AgentModel = agentModel;

        if (TryParseBoolean(GetValue(EnableControlSessionVariable), out var enableControlSession))
            config.EnableControlSession = enableControlSession;

        var defaultModel = GetValue(DefaultModelVariable);
        if (defaultModel is not null)
            config.DefaultModel = defaultModel;

        config.Provider ??= new CopilotProviderConfig();

        if (TryParseBoolean(GetValue(ProviderEnabledVariable), out var providerEnabled))
            config.Provider.Enabled = providerEnabled;

        var providerType = GetValue(ProviderTypeVariable);
        if (providerType is not null)
            config.Provider.Type = providerType;

        var providerBaseUrl = GetValue(ProviderBaseUrlVariable);
        if (providerBaseUrl is not null)
            config.Provider.BaseUrl = providerBaseUrl;

        var providerModel = GetValue(ProviderModelVariable);
        if (providerModel is not null)
            config.Provider.Model = providerModel;

        var providerApiKey = GetValue(ProviderApiKeyVariable);
        if (providerApiKey is not null)
            config.Provider.ApiKey = providerApiKey;

        var providerApiKeyEnvironmentVariable = GetValue(ProviderApiKeyEnvironmentVariable);
        if (providerApiKeyEnvironmentVariable is not null)
            config.Provider.ApiKeyEnvironmentVariable = providerApiKeyEnvironmentVariable;
    }

    private static string? GetValue(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool TryParseBoolean(string? value, out bool result)
    {
        if (bool.TryParse(value, out result))
            return true;

        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }
}
