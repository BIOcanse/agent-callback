using AgentCallback.Providers.Codex.DesktopIpc;

namespace AgentCallback.Tests;

public sealed class CodexDesktopCompatibilityGateTests
{
    [Fact]
    public void CurrentVerifiedVersion_IsAllowed()
    {
        var gate = new CodexDesktopCompatibilityGate(
            new FakeVersionDetector(["26.715.4045.0"]));

        var result = gate.Check();

        Assert.True(result.Compatible);
        Assert.Equal("26.715.4045.0", result.DetectedVersion);
    }

    [Fact]
    public void UnknownVersion_FailsClosed()
    {
        var gate = new CodexDesktopCompatibilityGate(
            new FakeVersionDetector(["99.0.0.0"]));

        var result = gate.Check();

        Assert.False(result.Compatible);
        Assert.Contains("fail-closed", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingVersion_FailsClosed()
    {
        var gate = new CodexDesktopCompatibilityGate(new FakeVersionDetector([]));

        var result = gate.Check();

        Assert.False(result.Compatible);
        Assert.Contains("could not be verified", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeVersionDetector : ICodexDesktopVersionDetector
    {
        private readonly IReadOnlyList<string> _versions;

        public FakeVersionDetector(IReadOnlyList<string> versions)
        {
            _versions = versions;
        }

        public IReadOnlyList<string> DetectRunningVersions() => _versions;
    }
}
