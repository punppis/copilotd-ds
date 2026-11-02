using System.CommandLine;
using Copilotd.Infrastructure;
using Copilotd.Models;
using Copilotd.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Copilotd.Commands;

public static class ConfigCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("config", "Manage copilotd configuration");

        var setOption = new Option<string?>("--set") { Description = "Set a config value in key=value format (e.g., repo_home=/path/to/repos)" };
        command.Options.Add(setOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var copilotCli = services.GetRequiredService<CopilotCliService>();
                var copilotTrust = services.GetRequiredService<CopilotTrustService>();
                var repoResolver = services.GetRequiredService<RepoPathResolver>();
                var stateStore = services.GetRequiredService<StateStore>();
                var setValue = parseResult.GetValue(setOption);

                if (string.IsNullOrWhiteSpace(setValue))
                {
                    // Display current config as a table
                    var config = stateStore.LoadConfig();
                    var table = new Table();
                    table.Border(TableBorder.Rounded);
                    table.ShowRowSeparators = true;
                    table.AddColumn(new TableColumn("[bold]Key[/]").NoWrap());
                    table.AddColumn(new TableColumn("[bold]Value[/]"));

                    table.AddRow("repo_home", Markup.Escape(config.RepoHome ?? "(not set)"));
                    table.AddRow("default_model", Markup.Escape(config.DefaultModel ?? "(not set)"));
                    table.AddRow("custom_prompt", Markup.Escape(string.IsNullOrEmpty(config.Prompt) ? "(not set)" : config.Prompt));
                    table.AddRow("session_name_format", Markup.Escape(string.IsNullOrWhiteSpace(config.SessionNameFormat) ? "(disabled)" : config.SessionNameFormat));
                    table.AddRow("current_user", Markup.Escape(config.CurrentUser ?? "(not set)"));
                    table.AddRow("enable_control_session", Markup.Escape(config.EnableControlSession.ToString().ToLowerInvariant()));
                    table.AddRow("enable_agents", Markup.Escape(config.EnableAgents.ToString().ToLowerInvariant()));
                    table.AddRow("agent_count", Markup.Escape(config.AgentCount.ToString()));
                    table.AddRow("agent_name_format", Markup.Escape(config.AgentNameFormat));
                    table.AddRow("agent_model", Markup.Escape(config.AgentModel));
                    table.AddRow("provider_enabled", Markup.Escape(config.Provider.Enabled.ToString().ToLowerInvariant()));
                    table.AddRow("provider_type", Markup.Escape(config.Provider.Type));
                    table.AddRow("provider_base_url", Markup.Escape(config.Provider.BaseUrl));
                    table.AddRow("provider_model", Markup.Escape(config.Provider.Model));
                    table.AddRow("provider_api_key", Markup.Escape(string.IsNullOrWhiteSpace(config.Provider.ApiKey) ? "(not set)" : "(configured)"));
                    table.AddRow("provider_api_key_env", Markup.Escape(config.Provider.ApiKeyEnvironmentVariable));
                    table.AddRow("provider_max_prompt_tokens", Markup.Escape(config.Provider.MaxPromptTokens?.ToString() ?? "(copilot default)"));
                    table.AddRow("provider_max_output_tokens", Markup.Escape(config.Provider.MaxOutputTokens?.ToString() ?? "(copilot default)"));
                    table.AddRow("max_instances", Markup.Escape(config.MaxInstances.ToString()));
                    table.AddRow("session_shutdown_delay_seconds", Markup.Escape(config.SessionShutdownDelaySeconds.ToString()));
                    table.AddRow("issue_rules", Markup.Escape($"{config.IssueRules.Count} rule(s)"));
                    table.AddRow("pull_request_rules", Markup.Escape($"{config.PullRequestRules.Count} rule(s)"));

                    if (config.IssueRules.Count > 0)
                    {
                        foreach (var (name, issueRule) in config.IssueRules)
                        {
                            var details = new List<string>();
                            if (issueRule.Assignee is not null) details.Add($"assignee={issueRule.Assignee}");
                            if (issueRule.Labels.Count > 0) details.Add($"labels={string.Join(",", issueRule.Labels)}");
                            if (issueRule.Milestone is not null) details.Add($"milestone={issueRule.Milestone}");
                            if (issueRule.Type is not null) details.Add($"type={issueRule.Type}");
                            if (issueRule.Repos.Count > 0) details.Add($"repos={string.Join(",", issueRule.Repos)}");
                            if (issueRule.Yolo) details.Add("yolo=true");
                            else
                            {
                                if (issueRule.AllowAllTools) details.Add("allow_all_tools=true");
                                if (issueRule.AllowAllUrls) details.Add("allow_all_urls=true");
                            }
                            if (issueRule.Model is not null) details.Add($"model={issueRule.Model}");
                            if (issueRule.ExtraPrompt is not null) details.Add($"extra_prompt={issueRule.ExtraPrompt}");
                            if (issueRule.CustomPrompt is not null) details.Add($"custom_prompt={issueRule.CustomPrompt}");
                            if (issueRule.CustomPrompt is not null) details.Add($"custom_prompt_mode={issueRule.CustomPromptMode.ToString().ToLowerInvariant()}");

                            table.AddRow(
                                Markup.Escape($"  issue_rule[{name}]"),
                                Markup.Escape(details.Count > 0 ? string.Join(", ", details) : "(no conditions)"));
                        }
                    }

                    if (config.PullRequestRules.Count > 0)
                    {
                        foreach (var (name, pullRequestRule) in config.PullRequestRules)
                        {
                            var details = new List<string>();
                            if (pullRequestRule.Assignee is not null) details.Add($"assignee={pullRequestRule.Assignee}");
                            if (pullRequestRule.Labels.Count > 0) details.Add($"labels={string.Join(",", pullRequestRule.Labels)}");
                            if (pullRequestRule.BaseBranch is not null) details.Add($"base={pullRequestRule.BaseBranch}");
                            if (pullRequestRule.HeadBranch is not null) details.Add($"head={pullRequestRule.HeadBranch}");
                            if (pullRequestRule.HeadRepo is not null) details.Add($"head_repo={pullRequestRule.HeadRepo}");
                            if (pullRequestRule.Draft is not null) details.Add($"draft={pullRequestRule.Draft.Value.ToString().ToLowerInvariant()}");
                            if (pullRequestRule.ReviewDecision is not null) details.Add($"review_decision={pullRequestRule.ReviewDecision}");
                            details.Add($"branch_strategy={pullRequestRule.BranchStrategy.ToString().ToLowerInvariant()}");
                            if (pullRequestRule.Repos.Count > 0) details.Add($"repos={string.Join(",", pullRequestRule.Repos)}");
                            if (pullRequestRule.Yolo) details.Add("yolo=true");
                            else
                            {
                                if (pullRequestRule.AllowAllTools) details.Add("allow_all_tools=true");
                                if (pullRequestRule.AllowAllUrls) details.Add("allow_all_urls=true");
                            }
                            if (pullRequestRule.Model is not null) details.Add($"model={pullRequestRule.Model}");
                            if (pullRequestRule.ExtraPrompt is not null) details.Add($"extra_prompt={pullRequestRule.ExtraPrompt}");
                            if (pullRequestRule.CustomPrompt is not null) details.Add($"custom_prompt={pullRequestRule.CustomPrompt}");
                            if (pullRequestRule.CustomPrompt is not null) details.Add($"custom_prompt_mode={pullRequestRule.CustomPromptMode.ToString().ToLowerInvariant()}");

                            table.AddRow(
                                Markup.Escape($"  pr_rule[{name}]"),
                                Markup.Escape(details.Count > 0 ? string.Join(", ", details) : "(no conditions)"));
                        }
                    }

                    AnsiConsole.Write(table);
                    return 0;
                }

                var eqIdx = setValue.IndexOf('=');
                if (eqIdx <= 0)
                {
                    ConsoleOutput.Error("Invalid format. Use --set key=value");
                    return 1;
                }

                var key = setValue[..eqIdx].Trim().ToLowerInvariant();
                var value = setValue[(eqIdx + 1)..].Trim();
                var cfg = stateStore.LoadConfig();

                switch (key)
                {
                    case "repo_home":
                        if (value.StartsWith('~'))
                        {
                            value = CopilotdPaths.ExpandUserProfile(value);
                        }
                        cfg.RepoHome = Path.GetFullPath(value);
                        CopilotTrustCommandHelper.EnsureTrustedFoldersForRepositories(
                            copilotTrust,
                            repoResolver,
                            copilotCli,
                            cfg,
                            stateStore.LoadState(),
                                cfg.IssueRules.Values.Cast<DispatchRuleOptions>()
                                    .Concat(cfg.PullRequestRules.Values)
                                    .SelectMany(dispatchRule => dispatchRule.Repos)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList());
                        ConsoleOutput.Success($"repo_home set to: {cfg.RepoHome}");
                        break;

                    case "custom_prompt":
                    case "prompt": // backward compat
                        cfg.Prompt = value;
                        ConsoleOutput.Success("custom_prompt updated.");
                        break;

                    case "default_model":
                    case "model": // convenience alias
                        cfg.DefaultModel = string.IsNullOrWhiteSpace(value) ? null : value;
                        ConsoleOutput.Success(cfg.DefaultModel is not null
                            ? $"default_model set to: {cfg.DefaultModel}"
                            : "default_model cleared.");
                        break;

                    case "session_name_format":
                        cfg.SessionNameFormat = value;
                        ConsoleOutput.Success(string.IsNullOrWhiteSpace(cfg.SessionNameFormat)
                            ? "session_name_format cleared; session naming disabled."
                            : $"session_name_format set to: {cfg.SessionNameFormat}");
                        break;

                    case "current_user":
                        cfg.CurrentUser = string.IsNullOrWhiteSpace(value) ? null : value;
                        ConsoleOutput.Success(cfg.CurrentUser is not null
                            ? $"current_user set to: {cfg.CurrentUser}"
                            : "current_user cleared.");
                        break;

                    case "enable_control_session":
                        if (TryParseBoolean(value, out var enableControlSession))
                        {
                            cfg.EnableControlSession = enableControlSession;
                            if (cfg.EnableControlSession)
                            {
                                CopilotTrustCommandHelper.EnsureTrustedFoldersForRepositories(
                                    copilotTrust,
                                    repoResolver,
                                    copilotCli,
                                    cfg,
                                    stateStore.LoadState(),
                                    cfg.IssueRules.Values.Cast<DispatchRuleOptions>()
                                        .Concat(cfg.PullRequestRules.Values)
                                        .SelectMany(dispatchRule => dispatchRule.Repos)
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .ToList());
                            }
                            ConsoleOutput.Success($"enable_control_session set to: {cfg.EnableControlSession.ToString().ToLowerInvariant()}");
                        }
                        else
                        {
                            ConsoleOutput.Error("enable_control_session must be true or false");
                            return 1;
                        }
                        break;

                    case "enable_agents":
                        if (TryParseBoolean(value, out var enableAgents))
                        {
                            cfg.EnableAgents = enableAgents;
                            if (cfg.EnableAgents && cfg.AgentCount > 0)
                            {
                                CopilotTrustCommandHelper.EnsureTrustedFoldersForRepositories(
                                    copilotTrust,
                                    repoResolver,
                                    copilotCli,
                                    cfg,
                                    stateStore.LoadState(),
                                    cfg.IssueRules.Values.Cast<DispatchRuleOptions>()
                                        .Concat(cfg.PullRequestRules.Values)
                                        .SelectMany(dispatchRule => dispatchRule.Repos)
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .ToList());
                            }
                            ConsoleOutput.Success($"enable_agents set to: {cfg.EnableAgents.ToString().ToLowerInvariant()}");
                        }
                        else
                        {
                            ConsoleOutput.Error("enable_agents must be true or false");
                            return 1;
                        }
                        break;

                    case "agent_count":
                    case "agents":
                        if (int.TryParse(value, out var agentCount)
                            && agentCount >= CopilotdConfig.MinAgentCount
                            && agentCount <= CopilotdConfig.MaxAgentCount)
                        {
                            cfg.AgentCount = agentCount;
                            ConsoleOutput.Success($"agent_count set to: {agentCount}");
                        }
                        else
                        {
                            ConsoleOutput.Error($"agent_count must be an integer between {CopilotdConfig.MinAgentCount} and {CopilotdConfig.MaxAgentCount}");
                            return 1;
                        }
                        break;

                    case "agent_name_format":
                        cfg.AgentNameFormat = string.IsNullOrWhiteSpace(value)
                            ? CopilotdConfig.DefaultAgentNameFormat
                            : value;
                        ConsoleOutput.Success($"agent_name_format set to: {cfg.AgentNameFormat}");
                        break;

                    case "agent_model":
                        cfg.AgentModel = string.IsNullOrWhiteSpace(value)
                            ? CopilotdConfig.DefaultAgentModel
                            : value;
                        ConsoleOutput.Success($"agent_model set to: {cfg.AgentModel}");
                        break;

                    case "provider_enabled":
                        if (TryParseBoolean(value, out var providerEnabled))
                        {
                            cfg.Provider.Enabled = providerEnabled;
                            ConsoleOutput.Success($"provider_enabled set to: {cfg.Provider.Enabled.ToString().ToLowerInvariant()}");
                        }
                        else
                        {
                            ConsoleOutput.Error("provider_enabled must be true or false");
                            return 1;
                        }
                        break;

                    case "provider_type":
                        cfg.Provider.Type = string.IsNullOrWhiteSpace(value)
                            ? CopilotProviderConfig.DefaultType
                            : value;
                        ConsoleOutput.Success($"provider_type set to: {cfg.Provider.Type}");
                        break;

                    case "provider_base_url":
                        cfg.Provider.BaseUrl = string.IsNullOrWhiteSpace(value)
                            ? CopilotProviderConfig.DefaultBaseUrl
                            : value;
                        ConsoleOutput.Success($"provider_base_url set to: {cfg.Provider.BaseUrl}");
                        break;

                    case "provider_model":
                        cfg.Provider.Model = string.IsNullOrWhiteSpace(value)
                            ? CopilotProviderConfig.DefaultModel
                            : value;
                        ConsoleOutput.Success($"provider_model set to: {cfg.Provider.Model}");
                        break;

                    case "provider_api_key":
                        cfg.Provider.ApiKey = string.IsNullOrWhiteSpace(value) ? null : value;
                        ConsoleOutput.Success(cfg.Provider.ApiKey is not null
                            ? "provider_api_key updated (prefer DEEPSEEK_API_KEY in .env to avoid storing secrets in config.json)."
                            : "provider_api_key cleared.");
                        break;

                    case "provider_api_key_env":
                        cfg.Provider.ApiKeyEnvironmentVariable = string.IsNullOrWhiteSpace(value)
                            ? CopilotProviderConfig.DefaultApiKeyEnvironmentVariable
                            : value;
                        ConsoleOutput.Success($"provider_api_key_env set to: {cfg.Provider.ApiKeyEnvironmentVariable}");
                        break;

                    case "provider_max_prompt_tokens":
                        if (TryParseOptionalPositiveInt(value, out var maxPromptTokens))
                        {
                            cfg.Provider.MaxPromptTokens = maxPromptTokens;
                            ConsoleOutput.Success(cfg.Provider.MaxPromptTokens is { } promptTokens
                                ? $"provider_max_prompt_tokens set to: {promptTokens}"
                                : "provider_max_prompt_tokens cleared.");
                        }
                        else
                        {
                            ConsoleOutput.Error("provider_max_prompt_tokens must be a positive integer or empty");
                            return 1;
                        }
                        break;

                    case "provider_max_output_tokens":
                        if (TryParseOptionalPositiveInt(value, out var maxOutputTokens))
                        {
                            cfg.Provider.MaxOutputTokens = maxOutputTokens;
                            ConsoleOutput.Success(cfg.Provider.MaxOutputTokens is { } outputTokens
                                ? $"provider_max_output_tokens set to: {outputTokens}"
                                : "provider_max_output_tokens cleared.");
                        }
                        else
                        {
                            ConsoleOutput.Error("provider_max_output_tokens must be a positive integer or empty");
                            return 1;
                        }
                        break;

                    case "max_instances":
                        if (int.TryParse(value, out var maxInst) && maxInst > 0)
                        {
                            cfg.MaxInstances = maxInst;
                            ConsoleOutput.Success($"max_instances set to: {maxInst}");
                        }
                        else
                        {
                            ConsoleOutput.Error("max_instances must be a positive integer");
                            return 1;
                        }
                        break;

                    case "session_shutdown_delay_seconds":
                    case "shutdown_delay_seconds":
                        if (int.TryParse(value, out var shutdownDelaySeconds) && shutdownDelaySeconds >= 0)
                        {
                            cfg.SessionShutdownDelaySeconds = shutdownDelaySeconds;
                            ConsoleOutput.Success($"session_shutdown_delay_seconds set to: {shutdownDelaySeconds}");
                        }
                        else
                        {
                            ConsoleOutput.Error("session_shutdown_delay_seconds must be a non-negative integer");
                            return 1;
                        }
                        break;

                    default:
                        ConsoleOutput.Error($"Unknown config key: {key}");
                        ConsoleOutput.Info("Valid keys: repo_home, default_model, custom_prompt, session_name_format, current_user, enable_control_session, enable_agents, agent_count, agent_name_format, agent_model, provider_enabled, provider_type, provider_base_url, provider_model, provider_api_key, provider_api_key_env, provider_max_prompt_tokens, provider_max_output_tokens, max_instances, session_shutdown_delay_seconds");
                        return 1;
                }

                stateStore.SaveConfig(cfg);
                return 0;
            }, logger);
        });

        return command;
    }

    private static bool TryParseBoolean(string value, out bool result)
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

    private static bool TryParseOptionalPositiveInt(string value, out int? result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = null;
            return true;
        }

        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            result = parsed;
            return true;
        }

        result = null;
        return false;
    }
}
