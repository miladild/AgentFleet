namespace AgentFleet;

internal sealed record OllamaUpdateRequest(string? Url = null, bool All = false);

/// <summary>
/// The web UI's Ollama versions panel. Like every other call, these are guarded by RequestGuard (the host and origin
/// check) in front of the whole app. Reading versions asks only the fleet's own machines; GitHub is asked by the check call alone.
/// </summary>
internal static class OllamaUpdateEndpoints
{
    public static void Map(IEndpointRouteBuilder app, OllamaUpdateService updates)
    {
        app.MapGet("/api/setup/ollama-versions", async (CancellationToken cancellationToken) =>
            Results.Json(await updates.ListAsync(cancellationToken)));

        app.MapPost("/api/setup/ollama-versions/check", async (CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Json(await updates.CheckLatestAsync(cancellationToken));
            }
            catch (OllamaUpdateException exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: exception.Status);
            }
        });

        // Which file is downloaded and what runs is decided in the backend from the checked release; the body only names the machine.
        app.MapPost("/api/setup/ollama-update", async (OllamaUpdateRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                if (!request.All && string.IsNullOrWhiteSpace(request.Url))
                {
                    return Results.BadRequest(new { error = "Name the machine (url) or ask for all of them." });
                }

                return Results.Json(await updates.StartAsync(request.All ? null : request.Url, cancellationToken), statusCode: StatusCodes.Status202Accepted);
            }
            catch (OllamaUpdateException exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: exception.Status);
            }
        });

        app.MapGet("/api/setup/ollama-updates", () => Results.Json(updates.List()));
    }
}
