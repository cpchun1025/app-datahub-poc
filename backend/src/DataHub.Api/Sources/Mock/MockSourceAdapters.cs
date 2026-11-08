using DataHub.Api.Configuration;
using DataHub.Api.Domain;

namespace DataHub.Api.Sources.Mock;

/// <summary>讓 mock 專用功能（tick、outage 模擬）可以由 catalog 揾到對應 upstream，而唔使知道具體 adapter 類型。</summary>
public interface IMockSourceAdapter
{
    MockUpstream Upstream { get; }
}

public sealed class MockStreamingSourceAdapter : IStreamingSourceAdapter, IMockSourceAdapter
{
    public MockStreamingSourceAdapter(MockSourceOptions options, MockUpstream upstream)
    {
        Upstream = upstream;
        Descriptor = new SourceDescriptor(
            options.SourceId,
            options.DisplayName,
            SourceKind.Streaming,
            IsMock: true,
            options.PriceDecimals,
            "Simulated snapshot + streaming feed (MOCK, in-process, no external service).");
    }

    public MockUpstream Upstream { get; }

    public SourceDescriptor Descriptor { get; }

    public Task<SourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        => Upstream.GetSnapshotAsync(cancellationToken);

    public ValueTask<IUpdateSubscription> SubscribeAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<IUpdateSubscription>(Upstream.Subscribe());
}

public sealed class MockPollingSourceAdapter : IPollingSourceAdapter, IMockSourceAdapter
{
    public MockPollingSourceAdapter(MockSourceOptions options, MockUpstream upstream)
    {
        Upstream = upstream;
        PollInterval = TimeSpan.FromSeconds(options.PollIntervalSeconds);
        Descriptor = new SourceDescriptor(
            options.SourceId,
            options.DisplayName,
            SourceKind.Polling,
            IsMock: true,
            options.PriceDecimals,
            "Simulated API snapshot + periodic polling (MOCK, in-process, no external service).");
    }

    public MockUpstream Upstream { get; }

    public SourceDescriptor Descriptor { get; }

    public TimeSpan PollInterval { get; }

    public Task<SourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        => Upstream.GetSnapshotAsync(cancellationToken);

    public Task<PollResult> PollAsync(long afterRevision, CancellationToken cancellationToken)
        => Task.FromResult(Upstream.Poll(afterRevision));
}
