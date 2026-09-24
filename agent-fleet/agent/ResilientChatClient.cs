using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

internal sealed class ResilientChatClient : DelegatingChatClient
{
    private readonly FleetNodeDefinition _node;
    private readonly FleetHealthMonitor _healthMonitor;
    private readonly IChatClient? _fallbackClient;
    private readonly ILogger _logger;

    public ResilientChatClient(
        IChatClient innerClient,
        FleetNodeDefinition node,
        FleetHealthMonitor healthMonitor,
        ILogger logger,
        IChatClient? fallbackClient = null)
        : base(innerClient)
    {
        _node = node;
        _healthMonitor = healthMonitor;
        _logger = logger;
        _fallbackClient = fallbackClient;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> messageList = Materialize(messages);
        IChatClient selectedClient = await SelectClientAsync(cancellationToken);

        try
        {
            return await selectedClient.GetResponseAsync(messageList, options, cancellationToken);
        }
        catch (Exception exception) when (CanFailOver(exception, selectedClient, cancellationToken))
        {
            string failure = exception is OperationCanceledException ? "request_timeout" : "request_failed";
            _healthMonitor.MarkUnavailable(_node.Name, failure);
            _logger.LogWarning(
                "Fleet node {FleetNode} failed before producing a response; using the hub fallback.",
                _node.Name);

            return await _fallbackClient!.GetResponseAsync(messageList, options, cancellationToken);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> messageList = Materialize(messages);
        IChatClient selectedClient = await SelectClientAsync(cancellationToken);

        // Do not retry a stream after output has started: that would duplicate partial answers.
        // A worker is instead replaced before dispatch when its cached health check is not ready.
        await foreach (ChatResponseUpdate update in selectedClient.GetStreamingResponseAsync(
            messageList,
            options,
            cancellationToken))
        {
            yield return update;
        }
    }

    private async Task<IChatClient> SelectClientAsync(CancellationToken cancellationToken)
    {
        if (_fallbackClient is null)
        {
            return InnerClient;
        }

        NodeHealthSnapshot status = await _healthMonitor.GetNodeAsync(_node.Name, cancellationToken);
        if (status.Ready)
        {
            return InnerClient;
        }

        _logger.LogWarning(
            "Fleet node {FleetNode} is not ready ({Failure}); using the hub fallback.",
            _node.Name,
            status.Failure ?? "unknown");
        return _fallbackClient;
    }

    private bool CanFailOver(Exception exception, IChatClient selectedClient, CancellationToken cancellationToken) =>
        _fallbackClient is not null &&
        ReferenceEquals(selectedClient, InnerClient) &&
        !cancellationToken.IsCancellationRequested &&
        (exception is HttpRequestException || exception is OperationCanceledException);

    private static IReadOnlyList<ChatMessage> Materialize(IEnumerable<ChatMessage> messages) =>
        messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
}
