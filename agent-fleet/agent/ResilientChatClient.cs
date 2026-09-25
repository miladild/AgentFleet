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
            CoolDown(exception);
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

        // A worker that fails before its first word (it went down since the last health check) is replaced by the
        // fallback, as for a non-streaming call. Once output has started, a failure is not retried: that would
        // duplicate a partial answer.
        IAsyncEnumerator<ChatResponseUpdate> stream = selectedClient
            .GetStreamingResponseAsync(messageList, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        bool hasFirst;
        try
        {
            hasFirst = await stream.MoveNextAsync();
        }
        catch (Exception exception) when (CanFailOver(exception, selectedClient, cancellationToken))
        {
            await stream.DisposeAsync();
            CoolDown(exception);
            _logger.LogWarning("Fleet node {FleetNode} failed before its first word; using the hub fallback.", _node.Name);
            stream = _fallbackClient!.GetStreamingResponseAsync(messageList, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
            hasFirst = await stream.MoveNextAsync();
        }

        try
        {
            if (!hasFirst)
            {
                yield break;
            }

            yield return stream.Current;
            while (await stream.MoveNextAsync())
            {
                yield return stream.Current;
            }
        }
        finally
        {
            await stream.DisposeAsync();
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

    // A machine that timed out is slow right now (a big model spilling out of graphics memory, or busy): it rests for
    // ten minutes. One that refused or broke rests for one, which is enough to skip it for the rest of this tool round.
    private void CoolDown(Exception exception)
    {
        bool timedOut = exception is OperationCanceledException;
        _healthMonitor.CoolDown(_node.Name, timedOut ? "slow" : "request_failed", timedOut ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(1));
    }

    private bool CanFailOver(Exception exception, IChatClient selectedClient, CancellationToken cancellationToken) =>
        _fallbackClient is not null &&
        ReferenceEquals(selectedClient, InnerClient) &&
        !cancellationToken.IsCancellationRequested &&
        // Unreachable, timed out, or Ollama itself failed (it crashed loading the model, ran out of memory): another
        // machine can answer. A model that does not support tools would fail anywhere the same way, so it is not retried.
        (exception is HttpRequestException || exception is OperationCanceledException ||
         (exception is OllamaSharp.Models.Exceptions.OllamaException && exception is not OllamaSharp.Models.Exceptions.ModelDoesNotSupportToolsException));

    private static IReadOnlyList<ChatMessage> Materialize(IEnumerable<ChatMessage> messages) =>
        messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
}
