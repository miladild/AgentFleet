using Microsoft.AspNetCore.Http;

namespace AgentFleet;

internal static class FleetConfigResponses
{
    public const string VersionConflictReason = "The settings changed since you opened them. Reload and try again.";

    public static IResult VersionConflict(string version) =>
        Results.Json(new { reason = VersionConflictReason, version }, statusCode: StatusCodes.Status409Conflict);

    public static IResult? ContextPatchFailure(FleetContextPatchResult result) => result.Status switch
    {
        FleetContextPatchStatus.NotFound => Results.NotFound(),
        FleetContextPatchStatus.Conflict => VersionConflict(result.Version),
        FleetContextPatchStatus.InvalidContextLength => Results.BadRequest(new { reason = result.Reason }),
        _ => null
    };
}
