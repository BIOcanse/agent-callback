using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentCallback.Triggers.Process;

public sealed partial class LinuxProcessInspector : IProcessInspector
{
    private const int ClockTicksConfigurationName = 2;
    private const int StartTimeTokenIndexAfterCommand = 19;
    private const int MaximumCommandLineBytes = 1024 * 1024;
    private static readonly Lazy<long> ClockTicksPerSecond = new(ResolveClockTicksPerSecond);
    private static readonly Lazy<DateTimeOffset> BootTimeUtc = new(ResolveBootTimeUtc);

    public ProcessSnapshot? TryGetSnapshot(int processId)
    {
        if (processId <= 0 || !OperatingSystem.IsLinux())
        {
            return null;
        }

        var processDirectory = $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}";
        var statPath = Path.Combine(processDirectory, "stat");
        try
        {
            var firstStartTicks = ReadStartTicks(statPath, processId);
            var executablePath = TryReadExecutablePath(processDirectory);
            var commandLine = TryReadCommandLine(processDirectory);
            var secondStartTicks = ReadStartTicks(statPath, processId);
            if (firstStartTicks != secondStartTicks)
            {
                return null;
            }

            var creationUtc = BootTimeUtc.Value.AddSeconds(
                firstStartTicks / (double)ClockTicksPerSecond.Value);
            return new ProcessSnapshot(
                processId,
                creationUtc,
                executablePath,
                commandLine);
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or
            DirectoryNotFoundException or
            IOException or
            UnauthorizedAccessException or
            FormatException or
            OverflowException)
        {
            return null;
        }
    }

    private static long ReadStartTicks(string statPath, int expectedProcessId)
    {
        var stat = File.ReadAllText(statPath, Encoding.UTF8);
        var commandEnd = stat.LastIndexOf(')');
        if (commandEnd <= 0 || commandEnd + 2 >= stat.Length)
        {
            throw new FormatException("Linux process stat record is malformed.");
        }

        var processIdEnd = stat.IndexOf(' ');
        if (processIdEnd <= 0 ||
            !int.TryParse(
                stat.AsSpan(0, processIdEnd),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var actualProcessId) ||
            actualProcessId != expectedProcessId)
        {
            throw new FormatException("Linux process stat PID does not match its path.");
        }

        var tokens = stat[(commandEnd + 2)..].Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length <= StartTimeTokenIndexAfterCommand ||
            !long.TryParse(
                tokens[StartTimeTokenIndexAfterCommand],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var startTicks) ||
            startTicks < 0)
        {
            throw new FormatException("Linux process start time is unavailable.");
        }

        return startTicks;
    }

    private static string? TryReadExecutablePath(string processDirectory)
    {
        try
        {
            return File.ResolveLinkTarget(
                Path.Combine(processDirectory, "exe"),
                returnFinalTarget: false)?.FullName;
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or
            DirectoryNotFoundException or
            IOException or
            UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryReadCommandLine(string processDirectory)
    {
        try
        {
            using var stream = new FileStream(
                Path.Combine(processDirectory, "cmdline"),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                4096,
                FileOptions.SequentialScan);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            var total = 0;
            while (total < MaximumCommandLineBytes)
            {
                var read = stream.Read(
                    chunk,
                    0,
                    Math.Min(chunk.Length, MaximumCommandLineBytes - total));
                if (read == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
                total += read;
            }

            if (stream.ReadByte() != -1)
            {
                return null;
            }

            var bytes = buffer.ToArray();
            if (bytes.Length == 0)
            {
                return null;
            }

            return Encoding.UTF8.GetString(bytes)
                .Replace('\0', ' ')
                .Trim();
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or
            DirectoryNotFoundException or
            IOException or
            UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long ResolveClockTicksPerSecond()
    {
        var result = Sysconf(ClockTicksConfigurationName);
        return result > 0
            ? result
            : throw new InvalidOperationException("Linux clock tick frequency is unavailable.");
    }

    private static DateTimeOffset ResolveBootTimeUtc()
    {
        foreach (var line in File.ReadLines("/proc/stat"))
        {
            if (line.StartsWith("btime ", StringComparison.Ordinal) &&
                long.TryParse(
                    line.AsSpan("btime ".Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }

        throw new InvalidOperationException("Linux boot time is unavailable.");
    }

    [LibraryImport("libc", EntryPoint = "sysconf", SetLastError = true)]
    private static partial long Sysconf(int name);
}
