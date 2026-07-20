namespace AgentCallback.Domain;

public sealed record ProviderStatus(
    string Provider,
    bool Available,
    bool Experimental,
    string Transport,
    string? ClientId,
    string? Error);

public enum DeliveryOutcomeKind
{
    Accepted,
    Retryable,
    Ambiguous,
    Failed
}

public sealed record CallbackDeliveryResult(
    DeliveryOutcomeKind Kind,
    string Transport,
    bool? AttachedToExisting,
    string ClientMessageId,
    string? Error)
{
    public static CallbackDeliveryResult Accepted(
        string transport,
        bool attachedToExisting,
        string clientMessageId) =>
        new(DeliveryOutcomeKind.Accepted, transport, attachedToExisting, clientMessageId, null);

    public static CallbackDeliveryResult Retryable(
        string transport,
        string clientMessageId,
        string error) =>
        new(DeliveryOutcomeKind.Retryable, transport, null, clientMessageId, error);

    public static CallbackDeliveryResult Ambiguous(
        string transport,
        string clientMessageId,
        string error) =>
        new(DeliveryOutcomeKind.Ambiguous, transport, null, clientMessageId, error);

    public static CallbackDeliveryResult Failed(
        string transport,
        string clientMessageId,
        string error) =>
        new(DeliveryOutcomeKind.Failed, transport, null, clientMessageId, error);
}

public interface IAgentProvider
{
    string Name { get; }

    Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<CallbackDeliveryResult> DeliverAsync(
        CallbackRecord callback,
        string message,
        CancellationToken cancellationToken);
}

public interface IAgentProviderRegistry
{
    IReadOnlyCollection<string> ProviderNames { get; }

    bool TryGet(string providerName, out IAgentProvider provider);

    IAgentProvider GetRequired(string providerName);
}

public sealed class AgentProviderRegistry : IAgentProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IAgentProvider> _providers;

    public AgentProviderRegistry(IEnumerable<IAgentProvider> providers)
    {
        var values = new Dictionary<string, IAgentProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Name))
            {
                throw new InvalidOperationException("Agent provider name cannot be empty.");
            }

            if (!values.TryAdd(provider.Name, provider))
            {
                throw new InvalidOperationException($"Duplicate agent provider: {provider.Name}");
            }
        }

        if (values.Count == 0)
        {
            throw new InvalidOperationException("At least one agent provider is required.");
        }

        _providers = values;
    }

    public IReadOnlyCollection<string> ProviderNames => _providers.Keys.ToArray();

    public bool TryGet(string providerName, out IAgentProvider provider) =>
        _providers.TryGetValue(providerName, out provider!);

    public IAgentProvider GetRequired(string providerName) =>
        TryGet(providerName, out var provider)
            ? provider
            : throw new InvalidOperationException(
                $"Unsupported agent provider '{providerName}'. Available providers: " +
                string.Join(", ", ProviderNames));
}
