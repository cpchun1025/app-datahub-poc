using DataHub.Api.Configuration;
using Microsoft.Extensions.Options;

namespace DataHub.Api.Sources.Mock;

public sealed class MockSourceCatalog : ISourceCatalog
{
    public MockSourceCatalog(IOptions<DataHubOptions> options, TimeProvider time)
    {
        Adapters = CreateAll(options, time);
    }

    public IReadOnlyList<ISourceAdapter> Adapters { get; }

    public static IReadOnlyList<ISourceAdapter> CreateAll(IOptions<DataHubOptions> options, TimeProvider time)
        => options.Value.Mock.Sources.Select(source => Create(source, time)).ToArray();

    public static ISourceAdapter Create(MockSourceOptions source, TimeProvider time)
    {
        var upstream = new MockUpstream(source, time);
        return source.Kind == Domain.SourceKind.Polling
            ? new MockPollingSourceAdapter(source, upstream)
            : new MockStreamingSourceAdapter(source, upstream);
    }
}
