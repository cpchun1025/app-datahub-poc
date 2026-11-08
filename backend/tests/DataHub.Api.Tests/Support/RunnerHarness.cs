using DataHub.Api.Configuration;
using DataHub.Api.Domain;
using DataHub.Api.Sources;
using DataHub.Api.Store;
using DataHub.Api.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DataHub.Api.Tests.Support;

/// <summary>
/// 用 FakeTimeProvider 驅動 <see cref="SourceRunner"/>：退避、排程、stale 全部靠推進假時間，測試唔使真係等。
/// </summary>
internal sealed class RunnerHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _run;

    public RunnerHarness(
        ISourceAdapter adapter,
        ILatestStateStore? store = null,
        DataHubOptions? options = null,
        RecordingTimeProvider? time = null,
        TimeSpan? schedulerTick = null)
    {
        Adapter = adapter;
        Time = time ?? new RecordingTimeProvider(TestData.T0);
        Options = options ?? TestData.NewOptions();
        Store = store ?? new LatestStateStore([adapter.Descriptor.SourceId]);
        Runner = new SourceRunner(
            adapter,
            Store,
            Options,
            Time,
            NullLogger.Instance,
            schedulerTick ?? TimeSpan.FromMilliseconds(100));
    }

    public ISourceAdapter Adapter { get; }

    public RecordingTimeProvider Time { get; }

    public DataHubOptions Options { get; }

    public ILatestStateStore Store { get; }

    public SourceRunner Runner { get; }

    public string SourceId => Runner.SourceId;

    public SourceStatus Status => Runner.GetStatus(Time.GetUtcNow());

    public SourceRuntimeState State => Runner.Runtime.State;

    public RunnerHarness Start()
    {
        _run = Runner.RunAsync(_cts.Token);
        return this;
    }

    public PriceRecord? Record(string instrumentId) => Store.Find(SourceId, instrumentId);

    /// <summary>
    /// 等到條件成立。每個回合推進一小段假時間，令 timer／退避邏輯前進；真實等待只係俾 background loop 有機會行，
    /// 正常情況毫秒內完成，只有失敗時先會用盡回合數。
    /// </summary>
    public async Task PumpUntil(Func<bool> condition, string because, TimeSpan? step = null, int maxSteps = 800)
    {
        var advance = step ?? TimeSpan.FromMilliseconds(100);
        for (var i = 0; i < maxSteps; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(1);
            if (condition())
            {
                return;
            }

            Time.Advance(advance);
        }

        Assert.Fail($"Condition not reached: {because}. status={Status}, state={State}");
    }

    /// <summary>唔推進時間，純粹等已經觸發嘅 background 工作完成。</summary>
    public async Task WaitUntil(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(1);
        }

        if (!condition())
        {
            Assert.Fail($"Condition not reached: {because}. status={Status}, state={State}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_run is not null)
        {
            try
            {
                await _run;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}
