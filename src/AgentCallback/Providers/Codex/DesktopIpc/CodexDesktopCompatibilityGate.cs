using System.Management;

namespace AgentCallback.Providers.Codex.DesktopIpc;

public sealed record CodexDesktopCompatibility(
    bool Compatible,
    string? DetectedVersion,
    string? Error);

public interface ICodexDesktopVersionDetector
{
    IReadOnlyList<string> DetectRunningVersions();
}

public sealed class CodexDesktopCompatibilityGate
{
    private const int MaximumConfiguredVersions = 32;
    private static readonly string[] DefaultSupportedVersions = ["26.715.4045.0"];
    private readonly ICodexDesktopVersionDetector _detector;

    public CodexDesktopCompatibilityGate(ICodexDesktopVersionDetector detector)
    {
        _detector = detector;
    }

    public CodexDesktopCompatibility Check()
    {
        var detected = _detector.DetectRunningVersions()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumConfiguredVersions)
            .ToArray();
        if (detected.Length == 0)
        {
            return new CodexDesktopCompatibility(
                false,
                null,
                "Codex Desktop package version could not be verified; experimental IPC is disabled.");
        }

        var supported = new HashSet<string>(DefaultSupportedVersions, StringComparer.OrdinalIgnoreCase);
        var configured = Environment.GetEnvironmentVariable(
            "AGENT_CALLBACK_CODEX_DESKTOP_ALLOWLIST");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var values = configured.Split(
                [',', ';'],
                MaximumConfiguredVersions,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var value in values)
            {
                if (IsFourPartVersion(value))
                {
                    supported.Add(value);
                }
            }
        }

        foreach (var version in detected)
        {
            if (supported.Contains(version))
            {
                return new CodexDesktopCompatibility(true, version, null);
            }
        }

        return new CodexDesktopCompatibility(
            false,
            string.Join(", ", detected),
            $"Unsupported Codex Desktop package version: {string.Join(", ", detected)}. " +
            "Experimental IPC is fail-closed until compatibility is explicitly allowed.");
    }

    private static bool IsFourPartVersion(string value) =>
        Version.TryParse(value, out var version) &&
        version.Major >= 0 &&
        version.Minor >= 0 &&
        version.Build >= 0 &&
        version.Revision >= 0;
}

public sealed class WindowsCodexDesktopVersionDetector : ICodexDesktopVersionDetector
{
    private const int MaximumProcesses = 64;
    private const string PackageMarker = "OpenAI.Codex_";

    public IReadOnlyList<string> DetectRunningVersions()
    {
        var versions = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ExecutablePath, CommandLine FROM Win32_Process WHERE Name = 'codex.exe'");
            using var results = searcher.Get();
            var observed = 0;
            foreach (ManagementObject process in results)
            {
                using (process)
                {
                    observed++;
                    if (observed > MaximumProcesses)
                    {
                        break;
                    }

                    var commandLine = process["CommandLine"] as string;
                    if (string.IsNullOrWhiteSpace(commandLine) ||
                        !commandLine.Contains("app-server", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var executablePath = process["ExecutablePath"] as string;
                    var version = ExtractPackageVersion(executablePath);
                    if (version is not null)
                    {
                        versions.Add(version);
                    }
                }
            }
        }
        catch (ManagementException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return versions;
    }

    private static string? ExtractPackageVersion(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var markerIndex = executablePath.IndexOf(PackageMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var versionStart = markerIndex + PackageMarker.Length;
        var versionEnd = executablePath.IndexOf('_', versionStart);
        if (versionEnd <= versionStart)
        {
            return null;
        }

        var value = executablePath[versionStart..versionEnd];
        return IsFourPartVersion(value) ? value : null;
    }

    private static bool IsFourPartVersion(string value) =>
        Version.TryParse(value, out var version) &&
        version.Build >= 0 &&
        version.Revision >= 0;
}
