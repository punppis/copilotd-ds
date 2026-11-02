using Copilotd.Infrastructure;
using Copilotd.Models;
using Microsoft.Extensions.Logging;

namespace Copilotd.Services;

/// <summary>
/// Outcome of one agent pool reconciliation pass.
/// </summary>
public sealed record AgentPoolReconcileResult(
    int DesiredCount,
    int RunningCount,
    IReadOnlyList<AgentSessionInfo> Launched,
    IReadOnlyList<AgentSessionInfo> Failed,
    int StoppedCount)
{
    public bool HasChanges => Launched.Count > 0 || Failed.Count > 0 || StoppedCount > 0;
}

/// <summary>
/// Keeps the configured number of always-on remote agent sessions alive. Agents are
/// independent of issue/PR dispatch: they are launched with <c>copilot --remote</c>,
/// named per host, and restarted whenever their process exits or the configured name
/// changes. Decreasing <see cref="CopilotdConfig.AgentCount"/> stops surplus agents.
/// </summary>
public sealed class AgentPoolSupervisor
{
    private static readonly TimeSpan RemoteUrlResolveTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FailedRetryDelay = TimeSpan.FromSeconds(60);

    private readonly ProcessManager _processManager;
    private readonly CopilotTrustService _copilotTrustService;
    private readonly GitHubRemoteSessionUrlResolver _remoteSessionUrls;
    private readonly ILogger<AgentPoolSupervisor> _logger;
    private bool _trustWarningShown;
    private bool _clampWarningLogged;

    public AgentPoolSupervisor(
        ProcessManager processManager,
        CopilotTrustService copilotTrustService,
        GitHubRemoteSessionUrlResolver remoteSessionUrls,
        ILogger<AgentPoolSupervisor> logger)
    {
        _processManager = processManager;
        _copilotTrustService = copilotTrustService;
        _remoteSessionUrls = remoteSessionUrls;
        _logger = logger;
    }

    public AgentPoolReconcileResult Reconcile(CopilotdConfig config, DaemonState state, string machineIdentifier)
    {
        var desiredCount = ResolveDesiredCount(config);
        var stopped = StopSurplusAgents(state, desiredCount);
        var launched = new List<AgentSessionInfo>();
        var failed = new List<AgentSessionInfo>();

        if (desiredCount == 0)
        {
            RemoveStoppedAgents(state);
            return new AgentPoolReconcileResult(0, 0, launched, failed, stopped);
        }

        var trust = CheckAgentsTrust();
        var running = 0;

        for (var index = 1; index <= desiredCount; index++)
        {
            var agent = state.Agents.FirstOrDefault(candidate => candidate.Index == index);
            if (agent is null)
            {
                agent = new AgentSessionInfo { Index = index, UpdatedAt = DateTimeOffset.UtcNow };
                state.Agents.Add(agent);
            }

            var expectedName = ProcessManager.BuildAgentSessionName(config, machineIdentifier, index, desiredCount);
            var needsRename = !string.Equals(agent.CopilotSessionName, expectedName, StringComparison.Ordinal);

            if (!needsRename
                && agent.Status == ControlSessionStatus.Failed
                && agent.UpdatedAt is { } failedAt
                && DateTimeOffset.UtcNow - failedAt < FailedRetryDelay)
            {
                _logger.LogDebug(
                    "Agent {Index} failed recently; retrying after {RetryAt}",
                    index,
                    failedAt + FailedRetryDelay);
                continue;
            }

            if (!needsRename && IsHealthy(agent, config.CurrentUser))
            {
                running++;
                continue;
            }

            if (needsRename)
                _logger.LogInformation("Agent {Index} session name changed; relaunching as {SessionName}", index, expectedName);

            TerminateIfTracked(agent);
            ClearProcess(agent);

            if (!trust.CanLaunch)
            {
                MarkFailed(agent, trust.Reason ?? "Copilot folder trust is required for agent sessions.");
                failed.Add(agent);
                continue;
            }

            var launchedAgent = _processManager.LaunchAgentSession(config, machineIdentifier, index, desiredCount);
            if (launchedAgent is null)
            {
                MarkFailed(agent, "Failed to launch the agent session.");
                failed.Add(agent);
                continue;
            }

            CopyLaunchResult(launchedAgent, agent);
            if (agent.Status == ControlSessionStatus.Running)
            {
                running++;
                launched.Add(agent);
            }
            else
            {
                failed.Add(agent);
            }
        }

        SortAgents(state);
        return new AgentPoolReconcileResult(desiredCount, running, launched, failed, stopped);
    }

    private int ResolveDesiredCount(CopilotdConfig config)
    {
        if (!config.EnableAgents)
            return 0;

        var desired = Math.Clamp(config.AgentCount, CopilotdConfig.MinAgentCount, CopilotdConfig.MaxAgentCount);
        if (desired != config.AgentCount && !_clampWarningLogged)
        {
            _clampWarningLogged = true;
            _logger.LogWarning(
                "agent_count {Configured} is outside the supported range {Min}-{Max}; using {Clamped}",
                config.AgentCount,
                CopilotdConfig.MinAgentCount,
                CopilotdConfig.MaxAgentCount,
                desired);
        }

        return desired;
    }

    private int StopSurplusAgents(DaemonState state, int desiredCount)
    {
        var stopped = 0;
        foreach (var agent in state.Agents.Where(candidate => candidate.Index > desiredCount || candidate.Index <= 0).ToList())
        {
            if (TryStopAgent(agent))
            {
                state.Agents.Remove(agent);
                stopped++;
            }
        }

        RemoveStoppedAgents(state);
        return stopped;
    }

    private bool TryStopAgent(AgentSessionInfo agent)
    {
        if (agent.ProcessId is null)
            return true;

        if (!_processManager.TerminateAgentSession(agent))
        {
            _logger.LogWarning("Failed to terminate agent {Index}; keeping tracked state for retry", agent.Index);
            return false;
        }

        agent.Status = ControlSessionStatus.Stopped;
        ClearProcess(agent);
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    private static void RemoveStoppedAgents(DaemonState state)
        => state.Agents.RemoveAll(agent =>
            agent.Status == ControlSessionStatus.Stopped
            && agent.ProcessId is null
            && agent.RootProcessId is null);

    private bool IsHealthy(AgentSessionInfo agent, string? currentUser)
    {
        if (agent.Status != ControlSessionStatus.Running)
            return false;

        if (_processManager.CheckAgentSession(agent) != ProcessLivenessResult.Alive)
            return false;

        if (_remoteSessionUrls.TryResolve(agent, currentUser) is not null)
            return true;

        var startedAt = agent.StartedAt ?? agent.UpdatedAt;
        return startedAt is { } started && DateTimeOffset.UtcNow - started < RemoteUrlResolveTimeout;
    }

    private void TerminateIfTracked(AgentSessionInfo agent)
    {
        if (agent.ProcessId is null)
            return;

        if (!_processManager.TerminateAgentSession(agent))
            _logger.LogWarning("Failed to terminate agent {Index} before relaunch", agent.Index);
    }

    private (bool CanLaunch, string? Reason) CheckAgentsTrust()
    {
        var trustCheck = _copilotTrustService.CheckTrustedFolders(
            _copilotTrustService.GetRequiredTrustedFoldersForAgents());

        switch (trustCheck.Status)
        {
            case CopilotTrustStatus.Trusted:
                return (true, null);

            case CopilotTrustStatus.Unknown:
                _logger.LogWarning(
                    "Could not verify Copilot folder trust for agent sessions: {Message}",
                    trustCheck.Message ?? "unknown trust verification error");
                return (true, null);

            case CopilotTrustStatus.Untrusted:
                var missingFolders = string.Join(", ", trustCheck.MissingFolders);
                if (!_trustWarningShown)
                {
                    _trustWarningShown = true;
                    ConsoleOutput.Warning(
                        "Agent session folder is not trusted by Copilot. Run 'copilotd init' or trust the folder manually before agent sessions can launch.");
                    _logger.LogWarning("Copilot folder trust missing for agent sessions: {Folders}", missingFolders);
                }
                else
                {
                    _logger.LogDebug("Copilot folder trust still missing for agent sessions: {Folders}", missingFolders);
                }

                return (false, $"Copilot folder trust is missing for: {missingFolders}");

            default:
                return (true, null);
        }
    }

    private static void MarkFailed(AgentSessionInfo agent, string reason)
    {
        agent.Status = ControlSessionStatus.Failed;
        agent.LastError = reason;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void ClearProcess(AgentSessionInfo agent)
    {
        agent.ProcessId = null;
        agent.ProcessStartTime = null;
        agent.RootProcessId = null;
        agent.RootProcessStartTime = null;
    }

    private static void CopyLaunchResult(AgentSessionInfo source, AgentSessionInfo target)
    {
        target.Index = source.Index;
        target.CopilotSessionId = source.CopilotSessionId;
        target.CopilotSessionName = source.CopilotSessionName;
        target.WorkingDirectory = source.WorkingDirectory;
        target.ProcessId = source.ProcessId;
        target.ProcessStartTime = source.ProcessStartTime;
        target.RootProcessId = source.RootProcessId;
        target.RootProcessStartTime = source.RootProcessStartTime;
        target.Status = source.Status;
        target.StartedAt = source.StartedAt;
        target.UpdatedAt = source.UpdatedAt;
        target.LastError = source.LastError;
    }

    private static void SortAgents(DaemonState state)
        => state.Agents.Sort((left, right) => left.Index.CompareTo(right.Index));
}
