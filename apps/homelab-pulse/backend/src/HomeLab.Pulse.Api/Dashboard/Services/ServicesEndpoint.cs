namespace HomeLab.Pulse.Api.Dashboard.Services;

internal static class ServicesEndpoint
{
    public static void MapDashboardServices(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/services.json", GetServices)
            .AllowAnonymous()
            .Produces<ServicesSnapshot>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static IResult GetServices(HttpContext context, ServicesPublication publication, TimeProvider clock)
    {
        context.Response.Headers.CacheControl = "no-store";
        return publication.Current is { } current && (current.ExpiresAt is null || current.ExpiresAt > clock.GetUtcNow())
            ? Results.Bytes(current.Json, "application/json")
            : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service details are temporarily unavailable.");
    }
}
