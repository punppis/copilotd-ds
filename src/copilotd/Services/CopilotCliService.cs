using System.Diagnostics;
using System.Text.RegularExpressions;
using Copilotd.Infrastructure;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace Copilotd.Services;

/// <summary>
/// Adapter for the copilot CLI. Handles dependency checks and auth validation.
/// </summary>
public sealed class CopilotCliService
{
    private static readonly Regex VersionTokenPattern = new(
        @"\bv?\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    /// <summary>
    /// First CLI version that retired the <c>--remote</c> startup flag in favor of
    /// the <c>remoteSessions</c> user setting.
    /// </summary>
    private static readonly Version MinimumRemoteSettingsVersion = new(1, 0, 103);

    public const string ExecutablePathVariable = "COPILOTD_COPILOT_PATH";
    public const string NoAutoUpdateVariable = "COPILOTD_COPILOT_NO_AUTO_UPDATE";

    private readonly ILogger<CopilotCliService> _logger;

    public CopilotCliService(ILogger<CopilotCliService> logger)
    {
        _logger = logger;
        var dotEnv = DotEnvLoader.Load();
        ExecutablePath = FirstNonEmpty(
            Environment.GetEnvironmentVariable(ExecutablePathVariable),
            dotEnv.GetValueOrDefault(ExecutablePathVariable)) ?? "copilot";
        NoAutoUpdate = IsTruthy(Environment.GetEnvironmentVariable(NoAutoUpdateVariable))
                       || IsTruthy(dotEnv.GetValueOrDefault(NoAutoUpdateVariable));
    }

    /// <summary>
    /// Copilot CLI executable used for version checks and launched sessions.
    /// Defaults to <c>copilot</c> on PATH and can be overridden with
    /// <see cref="ExecutablePathVariable"/> to pin a specific installation.
    /// </summary>
    public string ExecutablePath { get; }

    /// <summary>
    /// Whether every CLI invocation should pass <c>--no-auto-update</c>. Set this
    /// when <see cref="ExecutablePath"/> points at a pinned version so the CLI does
    /// not load a newer cached package.
    /// </summary>
    public bool NoAutoUpdate { get; }

    /// <summary>
    /// Prefix arguments shared by every CLI invocation (currently <c>--no-auto-update</c>).
    /// </summary>
    public string PrefixArguments => NoAutoUpdate ? "--no-auto-update " : "";

    public string BuildArguments(string arguments) => $"{PrefixArguments}{arguments}";

    /// <summary>
    /// Builds a Windows command line using the configured executable. PATH lookup
    /// is preserved when the default <c>copilot</c> name is used.
    /// </summary>
    public string BuildWindowsCommandLine(string arguments)
        => string.Equals(ExecutablePath, "copilot", StringComparison.OrdinalIgnoreCase)
            ? $"copilot {BuildArguments(arguments)}"
            : $"\"{ExecutablePath}\" {BuildArguments(arguments)}";

    /// <summary>
    /// Returns true if the configured copilot CLI executable is available.
    /// </summary>
    public bool IsAvailable()
    {
        try
        {
            var (exitCode, _) = RunCopilot("--version");
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the copilot CLI version string, or null if unavailable.
    /// </summary>
    public string? GetVersion()
    {
        try
        {
            var (exitCode, output) = RunCopilot("--version");
            if (exitCode != 0) return null;

            var trimmed = output.Trim();
            var idx = trimmed.IndexOf('\n');
            return idx > 0 ? trimmed[..idx].Trim() : trimmed;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to parse the installed copilot CLI version as SemVer, handling
    /// decorated version strings like "GitHub Copilot CLI 1.0.32.".
    /// </summary>
    public bool TryGetSemanticVersion(out NuGetVersion version, out string? versionDisplay)
    {
        versionDisplay = GetVersion();
        if (versionDisplay is null)
        {
            version = default!;
            return false;
        }

        if (VersionHelper.TryParse(versionDisplay, out version))
            return true;

        var match = VersionTokenPattern.Match(versionDisplay);
        if (!match.Success)
        {
            version = default!;
            return false;
        }

        return VersionHelper.TryParse(match.Value, out version);
    }

    /// <summary>
    /// Returns true when the installed CLI still supports the legacy <c>--remote</c>
    /// startup flag (versions before 1.0.103). Newer CLIs enable remote control via
    /// the <c>remoteSessions</c> setting instead.
    /// </summary>
    public bool SupportsLegacyRemoteFlag()
    {
        if (!TryGetSemanticVersion(out var version, out var versionDisplay))
        {
            _logger.LogWarning(
                "Could not determine the copilot CLI version; assuming the legacy --remote flag is unsupported");
            return false;
        }

        var usesLegacyFlag = version.Version < MinimumRemoteSettingsVersion;
        _logger.LogDebug(
            "copilot CLI {Version} enables remote sessions via {Mechanism}",
            versionDisplay,
            usesLegacyFlag ? "--remote" : $"the {CopilotSettingsService.RemoteSessionsSetting} setting");
        return usesLegacyFlag;
    }

    /// <summary>
    /// Best-effort check for copilot login status.
    /// Since copilot CLI has no "login status" command, we attempt a lightweight
    /// operation and check if it fails with auth errors.
    /// </summary>
    public bool IsLoggedIn()
    {
        try
        {
            // copilot --version works regardless of auth. We try copilot login
            // with a test approach — but that triggers the flow.
            // Best approach: attempt a minimal prompt that will fail fast if not authed.
            var (exitCode, output) = RunCopilot("--version");
            if (exitCode != 0)
                return false;

            // If version works, copilot is installed. Auth is harder to verify
            // without triggering the login flow. We'll assume if it's installed
            // and gh is authenticated, copilot should work (it uses gh auth).
            // The run loop will catch auth failures at dispatch time.
            _logger.LogDebug("copilot CLI available, assuming auth via gh");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking copilot auth");
            return false;
        }
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

    private static bool IsTruthy(string? value)
        => string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);

    private (int ExitCode, string Output) RunCopilot(string arguments)
    {
        _logger.LogDebug("Running: copilot {Args}", arguments);

        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            Arguments = BuildArguments(arguments),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
        {
            try { process.Kill(); } catch { }
        }
        var stderr = stderrTask.GetAwaiter().GetResult();

        var output = string.IsNullOrEmpty(stdout) ? stderr : stdout;
        return (process.ExitCode, output);
    }
}
