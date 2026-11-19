using DataHub.Api.Configuration;
using DataHub.Api.Endpoints;
using DataHub.Api.Persistence;
using DataHub.Api.Sources;
using DataHub.Api.Sources.Mock;
using DataHub.Api.Store;
using DataHub.Api.News;
using DataHub.Api.Sync;
using DataHub.Api.Csv;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace DataHub.Api.Hosting;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDataHub(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DataHubOptions>()
            .Bind(configuration.GetSection(DataHubOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DataHubOptions>, DataHubOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ServiceInfo>();

        // 來源 catalog 係唯一嘅替換點：測試或日後接真實來源時，只需要註冊另一個 ISourceCatalog。
        services.AddSingleton<ISourceCatalog, CompositeSourceCatalog>();
        services.AddSingleton<ILatestStateStore>(sp => new LatestStateStore(
            sp.GetRequiredService<ISourceCatalog>().Adapters.Select(adapter => adapter.Descriptor.SourceId)));
        services.AddSingleton<CompositionStore>();
        services.AddSingleton<CsvImportMonitor>();
        services.AddSingleton(sp => new CsvShowcaseReader(
            sp.GetRequiredService<IOptions<DataHubOptions>>().Value.CsvShowcase));
        services.AddSingleton<InstrumentReadService>();
        services.AddSingleton<ICheckpointStore, FileCheckpointStore>();
        services.AddSingleton<CheckpointService>();
        services.AddSingleton<SourceRegistry>();
        services.AddSingleton<SourceViewService>();
        services.AddSingleton<PriceReadService>();
        services.AddSingleton<NewsStore>();
        services.AddSingleton<MockNewsService>();
        services.AddSingleton<CsvCompositionIngestionService>();

        // 註冊次序就係啟動次序，停止次序相反：
        // 啟動：Recovery（要喺 Kestrel 接受請求之前完成）→ Checkpoint → Mock feed → Source sync
        // 停止：Source sync 先停（唔再寫入）→ Mock feed → Checkpoint 做最後保存 → Recovery
        services.AddHostedService<RecoveryHostedService>();
        services.AddHostedService<CheckpointBackgroundService>();
        services.AddHostedService(sp => sp.GetRequiredService<MockNewsService>());
        services.AddHostedService<MockFeedService>();
        services.AddHostedService<SourceSupervisor>();
        services.AddHostedService(sp => sp.GetRequiredService<CsvCompositionIngestionService>());

        services.ConfigureHttpJsonOptions(options => DataHubJson.Apply(options.SerializerOptions));
        services.AddProblemDetails();

        services.AddCors();
        services.AddOptions<CorsOptions>().Configure<IOptions<DataHubOptions>>((cors, dataHub) =>
        {
            var origins = dataHub.Value.Api.CorsAllowedOrigins;
            if (origins.Length > 0)
            {
                cors.AddDefaultPolicy(policy => policy
                    .WithOrigins(origins)
                    .WithMethods("GET", "POST")
                    .AllowAnyHeader());
            }
        });

        services.AddOpenApi("v1", options =>
        {
            options.AddSchemaTransformer((schema, context, _) =>
            {
                var type = context.JsonTypeInfo.Type;
                if (type == typeof(DateTimeOffset))
                    schema.Type = JsonSchemaType.String;
                else if (type == typeof(DateTimeOffset?))
                    schema.Type = JsonSchemaType.String | JsonSchemaType.Null;
                return Task.CompletedTask;
            });
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new()
                {
                    Title = "Data Hub PoC API",
                    Version = "v1",
                    Description = "Latest-state Data Hub PoC. Mock sources are synthetic and in-process; an optional CSV adapter can ingest operator-published synthetic demo files. "
                        + "Admin endpoints are unauthenticated and for PoC use only.",
                };
                return Task.CompletedTask;
            });
        });

        return services;
    }

    public static WebApplication UseDataHub(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<DataHubOptions>>().Value;

        // ServiceInfo 係 singleton，唔提早 resolve 嘅話，啟動時間會變成第一次 /api/health 嘅時間。
        _ = app.Services.GetRequiredService<ServiceInfo>();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler();
        }

        app.UseStatusCodePages();

        if (options.Api.CorsAllowedOrigins.Length > 0)
        {
            app.UseCors();
        }

        app.MapOpenApi();
        app.MapDataHubEndpoints();
        app.MapNewsEndpoints();

        if (app.Environment.IsDevelopment() || options.Api.SwaggerUiEnabled)
        {
            app.UseSwaggerUI(ui =>
            {
                ui.RoutePrefix = "swagger";
                ui.DocumentTitle = "Data Hub PoC API";
                ui.SwaggerEndpoint("/openapi/v1.json", "Data Hub PoC API v1");
            });
        }

        return app;
    }
}
