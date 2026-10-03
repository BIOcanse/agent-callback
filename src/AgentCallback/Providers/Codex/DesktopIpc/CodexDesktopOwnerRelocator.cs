#if WINDOWS
using System.ComponentModel;
using System.Diagnostics;

namespace AgentCallback.Providers.Codex.DesktopIpc;

/// <summary>
/// Opens <c>codex://threads/&lt;id&gt;</c> so Codex Desktop navigates to the conversation and its
/// window registers as the owner. Nothing is written to the conversation; the visible effect is
/// that Codex Desktop shows the target conversation.
/// </summary>
public sealed class CodexDesktopOwnerRelocator : ICodexOwnerRelocator
{
    private readonly TimeSpan _settleDelay;

    public CodexDesktopOwnerRelocator(TimeSpan settleDelay)
    {
        _settleDelay = settleDelay;
    }

    public async Task<bool> RelocateAsync(string threadId, CancellationToken cancellationToken)
    {
        // Only a well-formed conversation id may reach the shell.
        if (!Guid.TryParse(threadId, out var id))
        {
            return false;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo($"codex://threads/{id:D}")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return false;
        }

        await Task.Delay(_settleDelay, cancellationToken);
        return true;
    }
}
#endif
