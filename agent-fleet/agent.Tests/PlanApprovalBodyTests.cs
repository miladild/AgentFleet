using System.Text;
using Microsoft.AspNetCore.Http;

namespace AgentFleet.Tests;

public class PlanApprovalBodyTests
{
    private static DefaultHttpContext Request(string? contentType, string? body)
    {
        var context = new DefaultHttpContext();
        if (body is not null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
        }

        if (contentType is not null)
        {
            context.Request.ContentType = contentType;
        }

        return context;
    }

    [Fact]
    public async Task A_bare_post_with_no_body_and_no_content_type_means_no_options()
    {
        (PlanApprovalRequest? request, string? error) = await PlanApprovalBody.ReadAsync(Request(null, null).Request, CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.False(request.ExportToProject);
        Assert.Null(request.RecoveryScope);
        Assert.Null(request.HealChecks);
        Assert.Null(request.AutoRetries);
        Assert.Null(request.Review);
    }

    [Fact]
    public async Task An_empty_body_with_a_json_content_type_means_no_options()
    {
        (PlanApprovalRequest? request, string? error) = await PlanApprovalBody.ReadAsync(Request("application/json", string.Empty).Request, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(new PlanApprovalRequest(), request);
    }

    [Fact]
    public async Task A_body_with_options_is_read_as_the_plan_card_sends_it()
    {
        const string body = """{"exportToProject":true,"recoveryScope":"worker-only","healChecks":"off","autoRetries":3,"review":"on"}""";

        (PlanApprovalRequest? request, string? error) = await PlanApprovalBody.ReadAsync(Request("application/json", body).Request, CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.True(request.ExportToProject);
        Assert.Equal("worker-only", request.RecoveryScope);
        Assert.Equal("off", request.HealChecks);
        Assert.Equal(3, request.AutoRetries);
        Assert.Equal("on", request.Review);
    }

    [Fact]
    public async Task An_empty_json_object_means_no_options()
    {
        (PlanApprovalRequest? request, string? error) = await PlanApprovalBody.ReadAsync(Request("application/json", "{}").Request, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(new PlanApprovalRequest(), request);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_an_error_not_a_default()
    {
        (PlanApprovalRequest? request, string? error) = await PlanApprovalBody.ReadAsync(Request("application/json", "{not json").Request, CancellationToken.None);

        Assert.Null(request);
        Assert.Equal("The request body is not valid JSON.", error);
    }

    [Fact]
    public async Task A_body_without_a_json_content_type_is_left_alone()
    {
        const string body = """{"exportToProject":true}""";

        (PlanApprovalRequest? request, string? error) = await PlanApprovalBody.ReadAsync(Request("text/plain", body).Request, CancellationToken.None);

        Assert.Null(error);
        Assert.False(request!.ExportToProject);
    }
}
