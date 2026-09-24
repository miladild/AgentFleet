using Microsoft.Extensions.AI;

namespace AgentFleet;

/// <summary>
/// A chat client whose inner client can be replaced while the backend runs. The router is built from
/// the machines in fleet.config.json; when they change, a new router is built and swapped in here, so
/// the next request uses the new machines. A request already streaming keeps the client it started
/// with, and the old one is disposed a while later rather than cut off mid-answer.
/// </summary>
internal sealed class SwappableChatClient : IChatClient
{
    private static readonly TimeSpan RetireDelay = TimeSpan.FromMinutes(15);

    private volatile IChatClient _current;

    public SwappableChatClient(IChatClient initial) => _current = initial;

    public IChatClient Current => _current;

    public void Swap(IChatClient next)
    {
        IChatClient previous = Interlocked.Exchange(ref _current, next);
        if (!ReferenceEquals(previous, next))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(RetireDelay);
                previous.Dispose();
            });
        }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _current.GetResponseAsync(messages, options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _current.GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(SwappableChatClient) ? this : _current.GetService(serviceType, serviceKey);

    public void Dispose() => _current.Dispose();
}
