namespace AgentCallback.Infrastructure.Security;

public interface ISecretProtector
{
    byte[] Protect(string value);
    string Unprotect(byte[] value);
}
