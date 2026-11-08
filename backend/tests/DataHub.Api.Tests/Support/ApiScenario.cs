namespace DataHub.Api.Tests.Support;

/// <summary>
/// 一個測試獨享嘅完整 host：alpha（streaming）+ beta（polling）兩個 fake 來源，同一個 InstrumentId "SHARED" 喺兩邊都有。
/// Host 要等第一次存取 <see cref="Client"/> 先啟動，所以測試可以喺啟動前先擺好 upstream 狀態（例如 Down、HoldSnapshots）。
/// </summary>
internal sealed class ApiScenario : IAsyncDisposable
{
    private readonly bool _ownsCheckpoints;

    public ApiScenario(
        IReadOnlyDictionary<string, string?>? settings = null,
        TempDirectory? checkpoints = null,
        string? environment = null)
    {
        _ownsCheckpoints = checkpoints is null;
        Checkpoints = checkpoints ?? new TempDirectory();

        Alpha = new FakeStreamingAdapter("alpha", new FakeUpstream("USD"), priceDecimals: 2);
        Alpha.Upstream.Set("AAA", 10m);
        Alpha.Upstream.Set("SHARED", 100m);

        Beta = new FakePollingAdapter("beta", TimeSpan.FromMilliseconds(100), new FakeUpstream("EUR"), priceDecimals: 3);
        Beta.Upstream.Set("BBB", 20m);
        Beta.Upstream.Set("SHARED", 200m);

        Factory = new DataHubFactory(new FakeCatalog(Alpha, Beta), Checkpoints.FullPath, settings, environment);
    }

    public FakeStreamingAdapter Alpha { get; }

    public FakePollingAdapter Beta { get; }

    public TempDirectory Checkpoints { get; }

    public DataHubFactory Factory { get; }

    public HttpClient Client => Factory.Api;

    public string CheckpointFile => Checkpoints.File("checkpoint.json");

    /// <summary>啟動 host，等到兩個來源都完成首次同步（Live）。</summary>
    public async Task<HttpClient> StartAsync()
    {
        await Client.WaitForLiveAsync("alpha", "beta");
        return Client;
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (_ownsCheckpoints)
        {
            Checkpoints.Dispose();
        }
    }
}
