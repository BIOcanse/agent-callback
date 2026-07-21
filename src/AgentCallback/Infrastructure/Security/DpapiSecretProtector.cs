#if WINDOWS
using System.Security.Cryptography;
using System.Text;

namespace AgentCallback.Infrastructure.Security;

public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "agent-callback:v1"u8.ToArray();

    public byte[] Protect(string value) => ProtectedData.Protect(
        Encoding.UTF8.GetBytes(value),
        Entropy,
        DataProtectionScope.CurrentUser);

    public string Unprotect(byte[] value) => Encoding.UTF8.GetString(
        ProtectedData.Unprotect(value, Entropy, DataProtectionScope.CurrentUser));
}
#endif
