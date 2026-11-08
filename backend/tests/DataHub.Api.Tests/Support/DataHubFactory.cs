using DataHub.Api.Sources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataHub.Api.Tests.Support;

/// <summary>
/// 用真實 Program / DI / hosted services 起一個 Data Hub（TestServer，唔開真實 port）。
/// 預設用 fake 來源取代 mock catalog；傳 <c>null</c> 就保留真實嘅 mock 來源。
/// 預設設定令測試確定性：checkpoint 寫入暫存目錄、唔做定時 checkpoint、停用 stale 偵測、retry 間隔縮短。
/// </summary>
internal sealed class DataHubFactory : WebApplicationFactory<Program>
{
    private readonly ISourceCatalog? _catalog;
    private readonly Dictionary<string, string?> _settings;
    private readonly string _environment;
    private HttpClient? _api;

    public DataHubFactory(
        ISourceCatalog? catalog,
        string checkpointDirectory,
        IReadOnlyDictionary<string, string?>? settings = null,
        string? environment = null)
    {
        _catalog = catalog;
        _environment = environment ?? Environments.Production;
        _settings = new Dictionary<string, string?>
        {
            ["DataHub:Checkpoint:Directory"] = checkpointDirectory,
            ["DataHub:Checkpoint:IntervalSeconds"] = "3600",
            ["DataHub:Checkpoint:SaveOnShutdown"] = "false",
            ["DataHub:Freshness:StaleAfterSeconds"] = "0",
            ["DataHub:Snapshot:RetryInitialDelaySeconds"] = "1",
            ["DataHub:Snapshot:RetryMaxDelaySeconds"] = "2",
        };

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                _settings[key] = value;
            }
        }
    }

    /// <summary>第一次存取先會啟動 host（包括 RecoveryHostedService 同所有 source runner）。</summary>
    public HttpClient Api => _api ??= CreateClient();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

        if (_catalog is not null)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISourceCatalog>();
                services.AddSingleton(_catalog);
            });
        }
    }

    public override async ValueTask DisposeAsync()
    {
        _api?.Dispose();
        await base.DisposeAsync();
    }
}
