namespace HomeLab.Pulse.Api.Dashboard.Overview;

internal static class OverviewEndpoint
{
    public static void MapDashboardOverview(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/overview.json", GetOverview)
            .AllowAnonymous()
            .Produces<OverviewSnapshot>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static IResult GetOverview(HttpContext context, OverviewPublication publication, TimeProvider clock)
    {
        context.Response.Headers.CacheControl = "no-store";
        return publication.Current is { } current
            && (current.RefreshDeadline is null || current.RefreshDeadline > clock.GetUtcNow())
            ? Results.Bytes(current.Json, "application/json")
            : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Overview is temporarily unavailable.");
    }
}
