namespace HomeLab.Pulse.Api.Dashboard.Machines;

internal static class MachineEndpoint
{
    public static void MapDashboardMachine(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/machines/{alias}.json", GetMachine)
            .AllowAnonymous()
            .Produces<MachineSnapshot>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static IResult GetMachine(string alias, HttpContext context, MachineCatalog catalog,
        MachinePublication publication, TimeProvider clock)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!catalog.Aliases.Contains(alias))
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Machine was not found.", instance: string.Empty);
        }

        return publication.Find(alias) is { } current && (current.ExpiresAt is null || current.ExpiresAt > clock.GetUtcNow())
            ? Results.Bytes(current.Json, "application/json")
            : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Machine details are temporarily unavailable.");
    }
}
