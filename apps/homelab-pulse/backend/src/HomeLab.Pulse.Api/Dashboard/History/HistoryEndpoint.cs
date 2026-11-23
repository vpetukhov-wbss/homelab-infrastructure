namespace HomeLab.Pulse.Api.Dashboard.History;

internal static class HistoryEndpoint
{
    private static readonly HashSet<string> Windows = HistoryProjection.Windows.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);

    public static void MapDashboardHistory(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/history/fleet/{window}.json", GetFleet)
            .AllowAnonymous()
            .Produces<FleetHistorySnapshot>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static IResult GetFleet(
        string window,
        HttpContext context,
        HistoryPublication publication,
        TimeProvider clock)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Windows.Contains(window))
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "History window was not found.", instance: string.Empty);
        }

        return publication.FindFleet(window) is { } current && current.ExpiresAt > clock.GetUtcNow()
            ? Results.Bytes(current.Json, "application/json")
            : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "History is temporarily unavailable.");
    }
}
