using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentFleet.Tests;

internal static class PlanPromptComposition
{
    internal static string BuildPrompt(PlanRecord plan, PlanStep step, int attempt, string? lastFailure,
        string? fileContext = null, bool parallelGroup = false, bool durableContext = false,
        string? retryRoute = null, bool workerWorkspace = false, string? workerMachine = null,
        string? workerPlatform = null, string? brief = null, bool stepGuidance = false)
    {
        var data = new PlanStepPromptData(plan, step, attempt, lastFailure, fileContext, parallelGroup,
            durableContext, retryRoute, workerWorkspace, workerMachine, workerPlatform, brief, stepGuidance);
        return ComposeAsync(data.Parts.Text, data).GetAwaiter().GetResult();
    }

    internal static async Task<string> ComposeAsync(string prompt, PlanStepPromptData data)
    {
        var client = new PlanPromptGoldenTests.RecordingClient();
        IStepSession session = new FleetStepSession(new ChatClientAgent(client), "probe");
        session.ConfigurePrompt(data);
        await session.SendAsync(prompt, "standard", null, default);
        return Assert.Single(client.Requests).Messages.Single(message => message.Role == "user").Text;
    }
}
