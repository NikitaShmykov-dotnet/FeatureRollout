using System.Text.Json.Serialization;
using FeatureRollout;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;
using Npgsql;

namespace FeatureRollout.Api;

public static class FlagApi
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = Configuration.MaxBytes);
        var connectionString = builder.Configuration.GetConnectionString("Flags")
            ?? throw new InvalidOperationException("Set ConnectionStrings__Flags.");
        var token = builder.Configuration["Admin:Token"];
        if (token is not { Length: >= 16 and <= 256 } || token.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("Set Admin__Token to 16–256 characters without whitespace.");

        builder.Services.AddSingleton(new AdminCredentials(token, builder.Configuration["Admin:Actor"] ?? "demo-admin"));
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        builder.Services.AddSingleton<PublicationStore>();
        builder.Services.AddAuthentication("Bearer")
            .AddScheme<AuthenticationSchemeOptions, AdminAuthentication>("Bearer", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddProblemDetails();
        builder.Services.AddHealthChecks().AddCheck<DatabaseHealth>("postgres", timeout: TimeSpan.FromSeconds(3));
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.SerializerOptions.AllowDuplicateProperties = false;
            options.SerializerOptions.RespectRequiredConstructorParameters = true;
        });
        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer"
                };
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.ActionDescriptor.EndpointMetadata
                    .OfType<IAuthorizeData>().Any())
                {
                    var requirement = new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = []
                    };
                    operation.Security = [requirement];
                }
                return Task.CompletedTask;
            });
        });

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOpenApi();
        app.MapHealthChecks("/health");
        var admin = app.MapGroup("/v1/admin/environments").RequireAuthorization();

        admin.MapPut("/{name}", async (string name, PublicationStore store, CancellationToken ct) =>
        {
            if (!Configuration.IsKey(name))
                return Results.Problem("Invalid environment key.", statusCode: 400);

            bool created = await store.CreateEnvironment(name, ct);
            return created ? Results.Created($"/v1/admin/environments/{name}/history", null) : Results.NoContent();
        })
        .Produces(201)
        .Produces(204)
        .ProducesProblem(400)
        .ProducesProblem(401);

        admin.MapPost("/{name}/publications", async (string name, PublishRequest request, HttpContext context,
            PublicationStore store, ILogger<PublicationStore> logger, CancellationToken ct) =>
        {
            if (request.ExpectedRevision is < 0 or long.MaxValue)
                return Results.Problem("Invalid expected revision.", statusCode: 400);

            var snapshot = new Configuration(Configuration.CurrentSchema, name, request.ExpectedRevision + 1, request.Flags);
            var actor = context.User.Identity!.Name!;
            PublishResult result;
            try
            {
                result = await store.Publish(snapshot, actor, ct);
            }
            catch (InvalidDataException error)
            {
                return Results.Problem(error.Message, statusCode: 400);
            }

            if (result == PublishResult.MissingEnvironment)
                return Results.Problem(statusCode: 404);
            if (result == PublishResult.Conflict)
                return Results.Problem("The expected revision is stale or ahead of the current revision.", statusCode: 409);

            logger.LogInformation("Published {Environment} revision {Revision} by {Actor}", name, snapshot.Revision, actor);
            return Results.Created($"/v1/admin/environments/{name}/publications/{snapshot.Revision}",
                new PublishedRevision(snapshot.Revision));
        })
        .Produces<PublishedRevision>(201)
        .ProducesProblem(400)
        .ProducesProblem(401)
        .ProducesProblem(404)
        .ProducesProblem(409);

        admin.MapGet("/{name}/publications/{revision:long}", (string name, long revision, PublicationStore store,
            CancellationToken ct) => ReadSnapshot(name, revision, store, ct))
            .Produces<Configuration>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(404);

        admin.MapGet("/{name}/history", async (string name, long? before, PublicationStore store, CancellationToken ct) =>
            !Configuration.IsKey(name) || before is <= 0
                ? Results.Problem("Invalid environment key or cursor.", statusCode: 400)
                : Results.Ok(await store.History(name, before, ct)))
            .Produces<List<Publication>>()
            .ProducesProblem(400)
            .ProducesProblem(401);

        app.MapGet("/v1/config/{name}", (string name, PublicationStore store, CancellationToken ct) =>
            ReadSnapshot(name, null, store, ct))
            .Produces<Configuration>()
            .ProducesProblem(400)
            .ProducesProblem(404);

        return app;
    }

    private static async Task<IResult> ReadSnapshot(string name, long? revision, PublicationStore store, CancellationToken ct)
    {
        if (!Configuration.IsKey(name) || revision is <= 0)
            return Results.Problem("Invalid environment key or revision.", statusCode: 400);

        string? json = await store.Read(name, revision, ct);
        return json is null ? Results.Problem(statusCode: 404) : Results.Text(json, "application/json");
    }
}

public sealed record PublishRequest(long ExpectedRevision, Dictionary<string, FlagDefinition> Flags);
public sealed record PublishedRevision(long Revision);

public sealed class DatabaseHealth(PublicationStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        await store.CheckHealth(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
