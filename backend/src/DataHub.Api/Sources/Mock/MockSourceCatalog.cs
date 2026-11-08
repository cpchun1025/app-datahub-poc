using DataHub.Api.Configuration;
using Microsoft.Extensions.Options;

namespace DataHub.Api.Sources.Mock;

public sealed class MockSourceCatalog : ISourceCatalog
{
    public MockSourceCatalog(IOptions<DataHubOptions> options, TimeProvider time)
    {
        Adapters = options.Value.Mock.Sources
            .Select(source => Create(source, time))
            .ToArray();
    }

    public IReadOnlyList<ISourceAdapter> Adapters { get; }

    public static ISourceAdapter Create(MockSourceOptions source, TimeProvider time)
    {
        var upstream = new MockUpstream(source, time);
        return source.Kind == Domain.SourceKind.Polling
            ? new MockPollingSourceAdapter(source, upstream)
            : new MockStreamingSourceAdapter(source, upstream);
    }
}
