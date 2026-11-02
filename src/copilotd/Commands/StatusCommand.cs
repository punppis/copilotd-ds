using System.CommandLine;
using System.CommandLine.Parsing;
using Copilotd.Infrastructure;
using Copilotd.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Copilotd.Commands;

public static class StatusCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("status", "Show daemon status and tracked copilot sessions");

        var filterOption = new Option<string?>("--filter")
        {
            Description = SessionCommand.StatusFilterDescription
        };
        command.Options.Add(filterOption);

        var allOption = new Option<bool>("--all")
        {
            Description = "Include ended (completed/failed) sessions"
        };
        command.Options.Add(allOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            return await ConsoleOutput.RunWithErrorHandling(async () =>
            {
                var stateStore = services.GetRequiredService<StateStore>();
                var processManager = services.GetRequiredService<ProcessManager>();
                var remoteSessionUrls = services.GetRequiredService<GitHubRemoteSessionUrlResolver>();
                var logFileManager = services.GetRequiredService<LogFileManager>();

                // Daemon status header
                var daemonRunning = stateStore.IsLockHeld();
                var daemonInfo = daemonRunning ? stateStore.ReadDaemonPid() : null;
                var recoveredStaleControlSession = false;
                var recoveredStaleAgents = false;
                var controlLiveness = ProcessLivenessResult.Dead;
                var state = stateStore.WithStateLock(() =>
                {
                    var currentState = stateStore.LoadState();

                    if (currentState.ControlSession?.Status == ControlSessionStatus.Running)
                    {
                        var previousProcessId = currentState.ControlSession.ProcessId;
                        var previousProcessStartTime = currentState.ControlSession.ProcessStartTime;
                        controlLiveness = processManager.CheckControlSession(currentState.ControlSession);
                        if (controlLiveness is ProcessLivenessResult.Dead or ProcessLivenessResult.PidReused)
                        {
                            currentState.ControlSession.Status = ControlSessionStatus.Stopped;
                            currentState.ControlSession.ProcessId = null;
                            currentState.ControlSession.ProcessStartTime = null;
                            currentState.ControlSession.RootProcessId = null;
                            currentState.ControlSession.RootProcessStartTime = null;
                            currentState.ControlSession.UpdatedAt = DateTimeOffset.UtcNow;
                            stateStore.SaveState(currentState);
                            recoveredStaleControlSession = true;
                        }
                        else if (currentState.ControlSession.ProcessId != previousProcessId
                                 || currentState.ControlSession.ProcessStartTime != previousProcessStartTime)
                        {
                            stateStore.SaveState(currentState);
                        }
                    }

                    foreach (var agent in currentState.Agents)
                    {
                        if (agent.Status != ControlSessionStatus.Running)
                            continue;

                        var previousProcessId = agent.ProcessId;
                        var previousProcessStartTime = agent.ProcessStartTime;
                        var agentLiveness = processManager.CheckAgentSession(agent);
                        if (agentLiveness is ProcessLivenessResult.Dead or ProcessLivenessResult.PidReused)
                        {
                            agent.Status = ControlSessionStatus.Stopped;
                            agent.ProcessId = null;
                            agent.ProcessStartTime = null;
                            agent.RootProcessId = null;
                            agent.RootProcessStartTime = null;
                            agent.UpdatedAt = DateTimeOffset.UtcNow;
                            stateStore.SaveState(currentState);
                            recoveredStaleAgents = true;
                        }
                        else if (agent.ProcessId != previousProcessId
                                 || agent.ProcessStartTime != previousProcessStartTime)
                        {
                            stateStore.SaveState(currentState);
                        }
                    }

                    return currentState;
                }, ct);
                var config = stateStore.LoadConfig();

                AnsiConsole.MarkupLine(daemonRunning
                    ? "[green]● Daemon is running[/]"
                    : "[grey]○ Daemon is not running[/]");

                if (state.LastPollTime.HasValue)
                {
                    ConsoleOutput.Info($"  Last poll: {SessionCommand.FormatTime(state.LastPollTime.Value)}");
                }

                var watchedRepos = config.IssueRules.Values.Cast<DispatchRuleOptions>()
                    .Concat(config.PullRequestRules.Values)
                    .SelectMany(r => r.Repos)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (watchedRepos.Count > 0)
                {
                    ConsoleOutput.Info($"  Watching:  {string.Join(", ", watchedRepos)}");
                }

                ConsoleOutput.Info($"  Issue rules: {config.IssueRules.Count}");
                ConsoleOutput.Info($"  PR rules:    {config.PullRequestRules.Count}");
                if (daemonInfo is { LogInstanceId: { Length: > 0 } daemonLogInstanceId })
                    ConsoleOutput.Info($"  Logs:      {logFileManager.GetDaemonLogDirectoryForDisplay(daemonLogInstanceId)}");
                ConsoleOutput.Info($"  Machine:   {Environment.MachineName}");
                ConsoleOutput.Info($"  Machine ID:{(stateStore.GetMachineIdentifier() is { Length: > 0 } machineIdentifier ? $" {machineIdentifier}" : " (not assigned)")}");
                ConsoleOutput.Info($"  Provider:  {CopilotProviderEnvironment.Describe(config)}");

                // Control session status
                if (state.ControlSession is not null)
                {
                    if (recoveredStaleControlSession)
                        ConsoleOutput.Warning("Recovered stale control session -> Stopped");

                    var controlStatus = state.ControlSession.Status switch
                    {
                        ControlSessionStatus.Running => "[green]● Running[/]",
                        ControlSessionStatus.Starting => "[yellow]◐ Starting[/]",
                        ControlSessionStatus.Failed => "[red]✕ Failed[/]",
                        _ => "[grey]○ Stopped[/]",
                    };
                    var controlPid = state.ControlSession.ProcessId is { } pid ? $" (PID {pid})" : "";
                    AnsiConsole.MarkupLine($"  Control:   {controlStatus}{Markup.Escape(controlPid)}");

                    if (controlLiveness == ProcessLivenessResult.Alive)
                    {
                        var controlUrl = remoteSessionUrls.TryResolve(state.ControlSession, config.CurrentUser)
                            ?? "unavailable";
                        ConsoleOutput.Info("  Remote:");
                        ConsoleOutput.Info($"    {controlUrl}");
                    }

                }
                else if (config.EnableControlSession)
                {
                    AnsiConsole.MarkupLine("  Control:   [grey]○ Not started[/]");
                }

                if (config.EnableAgents && config.AgentCount > 0)
                {
                    if (recoveredStaleAgents)
                        ConsoleOutput.Warning("Recovered stale agent session(s) -> Stopped");

                    var desiredAgents = Math.Clamp(config.AgentCount, CopilotdConfig.MinAgentCount, CopilotdConfig.MaxAgentCount);
                    var trackedAgents = state.Agents.OrderBy(agent => agent.Index).ToList();
                    var runningAgents = trackedAgents.Count(agent => agent.Status == ControlSessionStatus.Running);
                    AnsiConsole.MarkupLine($"  Agents:    {runningAgents}/{desiredAgents} running");

                    foreach (var agent in trackedAgents)
                    {
                        var agentStatus = agent.Status switch
                        {
                            ControlSessionStatus.Running => "[green]● Running[/]",
                            ControlSessionStatus.Starting => "[yellow]◐ Starting[/]",
                            ControlSessionStatus.Failed => "[red]✕ Failed[/]",
                            _ => "[grey]○ Stopped[/]",
                        };
                        var agentPid = agent.ProcessId is { } agentPidValue ? $" (PID {agentPidValue})" : "";
                        AnsiConsole.MarkupLine(
                            $"    Agent {agent.Index}: {agentStatus}{Markup.Escape(agentPid)} {Markup.Escape(agent.CopilotSessionName ?? "(unnamed)")}");

                        if (agent.Status == ControlSessionStatus.Running)
                        {
                            var agentUrl = remoteSessionUrls.TryResolve(agent, config.CurrentUser)
                                ?? "remote URL unavailable";
                            ConsoleOutput.Info($"      {agentUrl}");
                        }

                        if (!string.IsNullOrWhiteSpace(agent.LastError))
                            ConsoleOutput.Info($"      {agent.LastError}");
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine("  Agents:    [grey]○ Disabled[/]");
                }

                Console.WriteLine();

                // Delegate session list rendering to the shared helper
                var filterValue = parseResult.GetValue(filterOption);
                var showAll = parseResult.GetValue(allOption);

                return SessionCommand.RenderSessionList(stateStore, processManager, remoteSessionUrls, config, filterValue, showAll);
            }, logger);
        });

        return command;
    }
}
