#pragma warning disable MAAI001

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentFleet.Tests;

public sealed class PlanStepPromptProviderTests
{
    [Theory]
    [InlineData("environment")]
    [InlineData("files")]
    [InlineData("continuation")]
    public async Task Each_provider_passes_messages_through_when_no_step_data_is_present(string section)
    {
        var agent = new ChatClientAgent(new PlanPromptGoldenTests.RecordingClient());
        AgentSession session = await agent.CreateSessionAsync();
        ChatMessage[] messages = [new(ChatRole.System, "Keep these instructions."), new(ChatRole.User, "Inspect the project.")];
        var context = new MessageAIContextProvider.InvokingContext(agent, session, messages);

        IEnumerable<ChatMessage> result = await Provider(section).InvokingAsync(context);

        Assert.Same(messages, result);
        Assert.Equal(0, session.StateBag.Count);
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("files")]
    [InlineData("continuation")]
    public async Task Each_provider_inserts_only_its_section_at_the_structured_offset(string section)
    {
        var data = PlanPromptGoldenTests.Scenario("combined").Data();
        var client = new PlanPromptGoldenTests.RecordingClient();
        var agent = new ChatClientAgent(client);
        AgentSession session = await agent.CreateSessionAsync();
        session.StateBag.SetValue(PlanStepPromptProvider.StateKey, data);
        var original = new ChatMessage(ChatRole.User, data.Parts.Text) { AuthorName = "synthetic-author", MessageId = "synthetic-message" };
        ChatMessage[] messages = [original];
        var context = new MessageAIContextProvider.InvokingContext(agent, session, messages);

        ChatMessage result = Assert.Single(await Provider(section).InvokingAsync(context));

        string full = await PlanPromptComposition.ComposeAsync(data.Parts.Text, data);
        int start = full.IndexOf("Unattended plan capability rules:", StringComparison.Ordinal);
        int step = full.IndexOf(data.Parts.Step, start, StringComparison.Ordinal);
        int guidance = step + data.Parts.Step.Length;
        int check = full.IndexOf(data.Parts.Check, guidance, StringComparison.Ordinal);
        string expectedSection = section switch
        {
            "environment" => full[start..step],
            "files" => full[guidance..check],
            _ => full[(check + data.Parts.Check.Length)..]
        };
        int offset = section switch
        {
            "environment" => data.Parts.Role.Length,
            "files" => data.Parts.Role.Length + data.Parts.Step.Length,
            _ => data.Parts.Text.Length
        };
        Assert.Equal(data.Parts.Text.Insert(offset, expectedSection), result.Text);
        Assert.Equal(data.Parts.Text, original.Text);
        Assert.NotSame(original, result);
        Assert.Equal(original.AuthorName, result.AuthorName);
        Assert.Equal(original.MessageId, result.MessageId);
        Assert.Equal(ChatRole.User, result.Role);
        Assert.IsType<TextContent>(Assert.Single(result.Contents));
    }

    [Fact]
    public async Task File_context_is_inert_when_guidance_is_off_and_no_files_were_read()
    {
        var agent = new ChatClientAgent(new PlanPromptGoldenTests.RecordingClient());
        AgentSession session = await agent.CreateSessionAsync();
        var data = PlanPromptGoldenTests.Scenario("first").Data();
        session.StateBag.SetValue(PlanStepPromptProvider.StateKey, data);
        ChatMessage[] messages = [new(ChatRole.User, data.Parts.Text)];
        var context = new MessageAIContextProvider.InvokingContext(agent, session, messages);

        ChatMessage result = Assert.Single(await new PlanFilesContextProvider().InvokingAsync(context));

        Assert.Equal(data.Parts.Text, result.Text);
    }

    [Fact]
    public async Task Repeated_role_and_check_text_in_project_data_does_not_change_insertion_positions()
    {
        var input = PlanPromptGoldenTests.Scenario("combined");
        string detail = input.Data().Parts.Role + input.Data().Parts.Check + "\nRepeated context: π and 😀.";
        string original = await PlanPromptComposition.ComposeAsync(input.Prompt(), input.Data());
        input = input with { Step = input.Step with { Detail = detail } };

        string actual = await PlanPromptComposition.ComposeAsync(input.Prompt(), input.Data());

        Assert.Equal(original.Replace("Reject empty input.", detail, StringComparison.Ordinal), actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Step_data_is_consumed_before_the_inner_run_and_never_reapplied_to_a_follow_up(bool failFirst)
    {
        var client = new SessionInspectingClient(failFirst);
        var agent = new ChatClientAgent(client);
        AgentSession? captured = null;
        AIAgent probe = agent.AsBuilder().Use(async (messages, session, options, next, cancellationToken) =>
        {
            captured = session;
            await next(messages, session, options, cancellationToken);
        }).Build();
        IStepSession stepSession = new FleetStepSession(probe, "probe");
        var input = PlanPromptGoldenTests.Scenario("combined");
        client.State = () => captured!.StateBag.Serialize().ToString();
        stepSession.ConfigurePrompt(input.Data());
        if (failFirst)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => stepSession.SendAsync(input.Prompt(), "standard", null, default));
            Assert.False(stepSession.HasHistory);
        }
        else
        {
            await stepSession.SendAsync(input.Prompt(), "standard", null, default);
        }
        await stepSession.SendAsync("A follow-up without step data.", "standard", null, default);

        Assert.All(client.States, state => Assert.DoesNotContain(PlanStepPromptProvider.StateKey, state));
        Assert.Equal("A follow-up without step data.", client.Requests[^1].Last(message => message.Role == ChatRole.User).Text);
        if (failFirst)
        {
            Assert.Single(client.Requests[^1]);
        }
    }

    [Fact]
    public async Task A_nudge_has_no_pending_step_data_and_keeps_the_initial_prompt_in_history()
    {
        var client = new PlanPromptGoldenTests.RecordingClient();
        IStepSession session = new FleetStepSession(new ChatClientAgent(client));
        var input = PlanPromptGoldenTests.Scenario("first");
        session.ConfigurePrompt(input.Data());

        await session.SendAsync(input.Prompt(), "standard", null, default);

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(FleetStepAgent.Nudge, client.Requests[1].Messages.Last(message => message.Role == "user").Text);
        Assert.Equal(client.Requests[0].Messages[0], client.Requests[1].Messages[0]);
    }

    private static MessageAIContextProvider Provider(string section) => section switch
    {
        "environment" => new PlanEnvironmentContextProvider(),
        "files" => new PlanFilesContextProvider(),
        _ => new PlanContinuationContextProvider()
    };

    private sealed class SessionInspectingClient(bool failFirst) : IChatClient
    {
        public Func<string> State { get; set; } = () => "";
        public List<string> States { get; } = [];
        public List<ChatMessage[]> Requests { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            States.Add(State());
            Requests.Add(messages.ToArray());
            if (failFirst && Requests.Count == 1) throw new InvalidOperationException("Synthetic model failure.");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
