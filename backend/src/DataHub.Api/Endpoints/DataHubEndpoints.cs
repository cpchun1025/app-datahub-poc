using System.Globalization;
using DataHub.Api.Configuration;
using DataHub.Api.Domain;
using DataHub.Api.Hosting;
using DataHub.Api.Persistence;
using DataHub.Api.Sources;
using DataHub.Api.Sources.Mock;
using DataHub.Api.Store;
using DataHub.Api.Sync;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace DataHub.Api.Endpoints;

public static class DataHubEndpoints
{
    private const int MaxOutageSeconds = 3600;

    public static IEndpointRouteBuilder MapDataHubEndpoints(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<IOptions<DataHubOptions>>().Value;

        app.MapGet("/", () => TypedResults.Ok(new
        {
            name = "Data Hub PoC (latest-state, MOCK sources)",
            health = "/api/health",
            openapi = "/openapi/v1.json",
            swagger = "/swagger",
        })).ExcludeFromDescription();

        var api = app.MapGroup("/api");

        api.MapGet("/health", GetHealth)
            .WithName("GetHealth")
            .WithTags("Health")
            .WithSummary("Liveness plus a compact view of source and checkpoint state.");

        api.MapGet("/sources", GetSources)
            .WithName("GetSources")
            .WithTags("Sources")
            .WithSummary("Per-source status, freshness and sync details.");

        api.MapGet("/prices", GetPrices)
            .WithName("GetPrices")
            .WithTags("Prices")
            .WithSummary("Server-side filtered, sorted and paginated latest prices from an immutable per-source view.")
            .WithDescription("sourceId filters one source (whole-id match, case-insensitive). instrumentId is a case-insensitive 'contains' search. "
                + "currency is a case-insensitive exact code; sourceStatus is a named status; minPrice/maxPrice are inclusive. "
                + "sortBy: sourceId (default), instrumentId, price, currency, sourceUpdatedAtUtc, receivedAtUtc. "
                + "sortDirection: asc (default) or desc. Nulls last, then ordinal sourceId/instrumentId ascending. "
                + "The same instrumentId from different sources is returned as separate rows. Requests are not a shared snapshot.");

        api.MapGet("/prices/export.csv", ExportPrices)
            .WithName("ExportPrices")
            .WithTags("Prices")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Export request-time prices as UTF-8 BOM CSV, with the same filters and ordering as the table.")
            .WithDescription("scope=page (default) exports the requested page; scope=all ignores pagination. "
                + "ExportMaxRows is enforced without truncation. Not a copy of a previously displayed or paused screen.");

        api.MapGet("/prices/{sourceId}/{instrumentId}", GetPrice)
            .WithName("GetPrice")
            .WithTags("Prices")
            .WithSummary("Latest price of one instrument from one source (exact match).");

        if (options.Api.AdminEnabled)
        {
            var admin = api.MapGroup("/admin").WithTags("Admin (PoC, unauthenticated)");

            admin.MapPost("/snapshot/{sourceId}", RefreshSnapshotAsync)
                .WithName("RefreshSnapshot")
                .WithSummary("Fetch a full snapshot from one source now and merge it into the store.");

            admin.MapPost("/checkpoint", SaveCheckpointAsync)
                .WithName("SaveCheckpoint")
                .WithSummary("Write a checkpoint file now.");

            admin.MapPost("/mock/{sourceId}/outage", SimulateOutage)
                .WithName("SimulateMockOutage")
                .WithSummary("Mock only: make a mock source unavailable for N seconds (0 ends the outage).");
        }

        return app;
    }

    private static Ok<HealthResponse> GetHealth(
        SourceViewService sources,
        CheckpointService checkpoints,
        ServiceInfo info,
        IOptions<DataHubOptions> options,
        TimeProvider time)
    {
        var views = sources.GetAll();
        var summary = new SourceSummaryDto(
            views.Count,
            views.Count(v => v.Status == SourceStatus.Live),
            views.Count(v => v.Status == SourceStatus.Initializing),
            views.Count(v => v.Status == SourceStatus.Recovering),
            views.Count(v => v.Status == SourceStatus.Stale),
            views.Count(v => v.Status == SourceStatus.Disconnected));

        return TypedResults.Ok(new HealthResponse(
            "ok",
            time.GetUtcNow(),
            info.StartedAtUtc,
            options.Value.UseMockSources,
            options.Value.Api.AdminEnabled,
            summary,
            ToDto(checkpoints.Status)));
    }

    private static Ok<SourceListResponse> GetSources(SourceViewService sources, TimeProvider time)
        => TypedResults.Ok(new SourceListResponse(time.GetUtcNow(), sources.GetAll().Select(ToDto).ToArray()));

    private static Results<Ok<PriceListResponse>, ValidationProblem, ProblemHttpResult> GetPrices(
        [AsParameters] PriceQueryParameters parameters,
        PriceReadService reader,
        IOptions<DataHubOptions> options,
        CancellationToken cancellationToken)
    {
        if (!parameters.TryParse(options.Value.Api, out var query, out var status, out var errors))
            return TypedResults.ValidationProblem(errors);
        if (!reader.HasSource(query.SourceId))
            return UnknownSource(query.SourceId);
        return TypedResults.Ok(reader.Read(query, status, cancellationToken));
    }

    private static IResult ExportPrices(
        [AsParameters] PriceQueryParameters parameters,
        string? scope,
        PriceReadService reader,
        IOptions<DataHubOptions> options,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        var all = string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase);
        var valid = parameters.TryParse(options.Value.Api, out var query, out var status, out var errors, ignorePagination: all);
        if (scope is not null && !all && !scope.Equals("page", StringComparison.OrdinalIgnoreCase))
            errors["scope"] = ["Use page or all."];
        if (!valid || errors.Count > 0)
            return TypedResults.ValidationProblem(errors);
        if (!reader.HasSource(query.SourceId))
            return UnknownSource(query.SourceId);

        PriceListResponse data;
        try
        {
            data = reader.Read(query, status, cancellationToken, all, options.Value.Api.ExportMaxRows);
        }
        catch (PriceExportLimitException ex)
        {
            return TypedResults.Problem(title: "Export row limit exceeded", detail: ex.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var stamp = data.ServerTimeUtc.ToUniversalTime().ToString("yyyyMMddTHHmmssfff'Z'", CultureInfo.InvariantCulture);
        var filename = $"prices-{(all ? "all" : "page")}-{stamp}.csv";
        return Results.Stream(stream => PricesCsv.WriteAsync(stream, data.Items, cancellationToken),
            contentType: "text/csv; charset=utf-8", fileDownloadName: filename);
    }

    private static ProblemHttpResult UnknownSource(string? sourceId) => TypedResults.Problem(
        title: "Unknown source", detail: $"Source '{sourceId}' is not registered.", statusCode: StatusCodes.Status404NotFound);

    private static Results<Ok<PriceDto>, ProblemHttpResult> GetPrice(
        string sourceId,
        string instrumentId,
        ILatestStateStore store,
        SourceViewService sources)
    {
        var view = sources.Find(sourceId);
        if (view is null)
        {
            return TypedResults.Problem(
                title: "Unknown source",
                detail: $"Source '{sourceId}' is not registered.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var record = store.Find(view.Descriptor.SourceId, instrumentId);
        if (record is null)
        {
            return TypedResults.Problem(
                title: "Instrument not found",
                detail: $"Source '{view.Descriptor.SourceId}' has no instrument '{instrumentId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return TypedResults.Ok(PriceDto.From(record, view.Status));
    }

    private static async Task<Results<Ok<SnapshotRefreshResponse>, ProblemHttpResult>> RefreshSnapshotAsync(
        string sourceId,
        SourceRegistry registry,
        IHostApplicationLifetime lifetime)
    {
        var runner = registry.Find(sourceId);
        if (runner is null)
        {
            return TypedResults.Problem(
                title: "Unknown source",
                detail: $"Source '{sourceId}' is not registered.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // 用 ApplicationStopping 而唔係 RequestAborted：client 中途斷線唔應該打斷一個已經開始嘅 snapshot。
        var result = await runner.RefreshSnapshotAsync(SnapshotTrigger.Manual, lifetime.ApplicationStopping);
        switch (result.Outcome)
        {
            case SnapshotRefreshOutcome.Succeeded:
                var applied = result.Applied!.Value;
                return TypedResults.Ok(new SnapshotRefreshResponse(
                    runner.SourceId, applied.Added, applied.Updated, applied.Removed, applied.KeptNewer, applied.TotalRecords, applied.Skipped));

            case SnapshotRefreshOutcome.AlreadyRunning:
                return TypedResults.Problem(
                    title: "Snapshot already running",
                    detail: $"A snapshot for '{runner.SourceId}' is already in progress.",
                    statusCode: StatusCodes.Status409Conflict);

            default:
                return TypedResults.Problem(
                    title: "Source snapshot failed",
                    detail: result.Error,
                    statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<Results<Ok<CheckpointRunResponse>, ProblemHttpResult>> SaveCheckpointAsync(
        CheckpointService checkpoints,
        IHostApplicationLifetime lifetime)
    {
        var result = await checkpoints.SaveAsync(CheckpointTrigger.Manual, lifetime.ApplicationStopping);
        return result.Outcome switch
        {
            CheckpointOutcome.Saved => TypedResults.Ok(new CheckpointRunResponse(result.Outcome.ToString(), ToDto(result.Status))),
            CheckpointOutcome.Busy => TypedResults.Problem(
                title: "Checkpoint already running",
                detail: "A checkpoint save is already in progress.",
                statusCode: StatusCodes.Status409Conflict),
            CheckpointOutcome.Disabled => TypedResults.Problem(
                title: "Checkpoint disabled",
                detail: "DataHub:Checkpoint:Enabled is false.",
                statusCode: StatusCodes.Status409Conflict),
            CheckpointOutcome.SkippedEmpty => TypedResults.Problem(
                title: "Nothing to checkpoint",
                detail: "No source has data yet; an empty checkpoint is never written.",
                statusCode: StatusCodes.Status409Conflict),
            _ => TypedResults.Problem(
                title: "Checkpoint save failed",
                detail: result.Error,
                statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static Results<Ok<MockOutageResponse>, ValidationProblem, ProblemHttpResult> SimulateOutage(
        string sourceId,
        int? seconds,
        ISourceCatalog catalog)
    {
        var adapter = catalog.Adapters.FirstOrDefault(a => string.Equals(a.Descriptor.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
        {
            return TypedResults.Problem(
                title: "Unknown source",
                detail: $"Source '{sourceId}' is not registered.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (adapter is not IMockSourceAdapter mock)
        {
            return TypedResults.Problem(
                title: "Not a mock source",
                detail: "Outage simulation is only available for mock sources.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var duration = seconds ?? 20;
        if (duration is < 0 or > MaxOutageSeconds)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["seconds"] = [$"seconds must be between 0 and {MaxOutageSeconds} (0 ends the outage)."],
            });
        }

        var until = mock.Upstream.SimulateOutage(TimeSpan.FromSeconds(duration));
        return TypedResults.Ok(new MockOutageResponse(adapter.Descriptor.SourceId, duration, until));
    }

    private static SourceDto ToDto(SourceView view)
    {
        var runtime = view.Runtime;
        var (error, errorAt) = LatestError(runtime);
        return new SourceDto(
            view.Descriptor.SourceId,
            view.Descriptor.DisplayName,
            view.Descriptor.Kind,
            view.Descriptor.IsMock,
            view.Descriptor.Description,
            view.Descriptor.PriceDecimals,
            view.Status,
            view.Data.RecordCount,
            runtime.RestoredFromCheckpoint,
            runtime.SyncedSinceStart,
            runtime.SnapshotInProgress,
            view.Data.LastSnapshotAtUtc,
            view.Data.LastUpdateReceivedAtUtc,
            runtime.LastFeedActivityAtUtc,
            runtime.NextSnapshotDueAtUtc,
            error,
            errorAt);
    }

    private static (string? Error, DateTimeOffset? At) LatestError(SourceRuntimeState runtime)
    {
        if (runtime.LastSnapshotError is null)
        {
            return (runtime.LastFeedError, runtime.LastFeedErrorAtUtc);
        }

        if (runtime.LastFeedError is null)
        {
            return (runtime.LastSnapshotError, runtime.LastSnapshotErrorAtUtc);
        }

        return runtime.LastFeedErrorAtUtc > runtime.LastSnapshotErrorAtUtc
            ? (runtime.LastFeedError, runtime.LastFeedErrorAtUtc)
            : (runtime.LastSnapshotError, runtime.LastSnapshotErrorAtUtc);
    }

    private static CheckpointDto ToDto(CheckpointStatus status) => new(
        status.Enabled,
        status.LoadedFrom,
        status.LoadedSavedAtUtc,
        status.LoadedRecordCount,
        status.LoadWarnings,
        status.SaveInProgress,
        status.LastAttemptAtUtc,
        status.LastSuccessAtUtc,
        status.LastSuccessRecordCount,
        status.LastSuccessBytes,
        status.LastSuccessDurationMs,
        status.LastErrorAtUtc,
        status.LastError,
        status.ConsecutiveFailures);
}
