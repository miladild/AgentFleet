using System.Text.Json;

namespace AgentFleet;

// The Live page resumes a blocked plan with a bare POST: no body and no content type, like its other plan buttons. A bound
// body parameter answers that with 415 even when an empty body is allowed (the content type is checked first), so the
// options are read here, and only when a JSON body is really there. Without one they are all left out, which keeps
// the settings the plan already has.
internal static class PlanApprovalBody
{
    internal static async Task<(PlanApprovalRequest? Request, string? Error)> ReadAsync(HttpRequest http, CancellationToken cancellationToken)
    {
        if (http.ContentLength is null or 0 || !http.HasJsonContentType())
        {
            return (new PlanApprovalRequest(), null);
        }

        try
        {
            return (await http.ReadFromJsonAsync<PlanApprovalRequest>(cancellationToken) ?? new PlanApprovalRequest(), null);
        }
        catch (JsonException)
        {
            return (null, "The request body is not valid JSON.");
        }
    }
}
