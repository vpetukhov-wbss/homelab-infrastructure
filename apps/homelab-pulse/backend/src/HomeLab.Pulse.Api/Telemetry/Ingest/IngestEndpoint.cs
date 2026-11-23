using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.OpenApi;

namespace HomeLab.Pulse.Api.Telemetry.Ingest;

internal static class IngestEndpoint
{
    public const int MaxBodyBytes = 64 * 1024;

    public static void MapTelemetryIngest(this WebApplication app) =>
        app.MapPost("/internal/v1/telemetry", HandleAsync)
            .RequireAuthorization()
            .WithName("IngestTelemetryV1")
            .WithTags("Telemetry")
            .WithSummary("Accept a host sample from an authenticated agent.")
            .AddOpenApiOperationTransformer(async (operation, context, cancellationToken) =>
            {
                var schema = await context.GetOrCreateSchemaAsync(typeof(JsonTelemetryBody), cancellationToken: cancellationToken);
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Description = "Telemetry v1 JSON, up to 64 KiB. Send the agent's bearer credential in the Authorization header.",
                    Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
                    {
                        ["application/json"] = new() { Schema = schema },
                    },
                };
            })
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> HandleAsync(HttpContext context, IngestHandler handler, CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (request.ContentLength > MaxBodyBytes)
        {
            return PayloadTooLarge();
        }

        if (!request.HasJsonContentType())
        {
            return TypedResults.Problem(title: "A JSON body is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var body = new byte[MaxBodyBytes + 1];
        int size;
        try
        {
            size = await request.Body.ReadAtLeastAsync(body, body.Length, false, cancellationToken);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode is 400 or 413)
        {
            return exception.StatusCode == 413
                ? PayloadTooLarge()
                : TypedResults.Problem(title: "The request body is invalid.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (size > MaxBodyBytes)
        {
            return PayloadTooLarge();
        }

        var agentId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("The authenticated agent has no identifier.");
        var result = await handler.HandleAsync(agentId, body.AsMemory(0, size), cancellationToken);
        return result switch
        {
            IngestResult.Accepted => TypedResults.NoContent(),
            IngestResult.InvalidJson => TypedResults.Problem(
                title: "The body is not valid JSON.", statusCode: StatusCodes.Status400BadRequest),
            IngestResult.InvalidSample => TypedResults.Problem(
                title: "The sample violates the telemetry v1 contract or time window.",
                statusCode: StatusCodes.Status422UnprocessableEntity),
            IngestResult.StorageUnavailable => TypedResults.Problem(
                title: "Telemetry storage is temporarily unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => throw new InvalidOperationException("The ingest outcome is unknown."),
        };
    }

    private static ProblemHttpResult PayloadTooLarge() =>
        TypedResults.Problem(title: "Telemetry samples are limited to 64 KiB.", statusCode: StatusCodes.Status413PayloadTooLarge);

    // OpenAPI describes the envelope. Validation uses the complete embedded v1 schema.
    private sealed record JsonTelemetryBody(int SchemaVersion, Guid SampleId, DateTimeOffset ObservedAt, string Kind, System.Text.Json.JsonElement Values);
}
