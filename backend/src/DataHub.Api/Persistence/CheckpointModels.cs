using DataHub.Api.Store;
using DataHub.Api.News;

namespace DataHub.Api.Persistence;

/// <summary>要保存／已恢復嘅內容。<see cref="Version"/> 係 capture 當刻 store 嘅版本。</summary>
public sealed record CheckpointPayload(
    long Version,
    DateTimeOffset SavedAtUtc,
    IReadOnlyList<SourceData> Sources)
{
    public long RecordCount => Sources.Sum(source => (long)source.RecordCount);
    public IReadOnlyList<NewsRecord> News { get; init; } = [];
    public IReadOnlyList<NewsIdentity> NewsIdentities { get; init; } = [];
}

public sealed record CheckpointLoadResult(
    CheckpointPayload? Payload,
    string? LoadedFrom,
    IReadOnlyList<string> Warnings);

public sealed record CheckpointSaveResult(long Bytes, long RecordCount, TimeSpan Duration);

public class CheckpointFormatException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class FutureCheckpointFormatException(int version)
    : CheckpointFormatException($"Unsupported future checkpoint schemaVersion {version}; saving is disabled to protect the file.");

public interface ICheckpointStore
{
    /// <summary>載入 checkpoint：primary → backup → 無。任何損壞都只會變成 warning，唔會拋出例外令啟動失敗。</summary>
    Task<CheckpointLoadResult> LoadAsync(CancellationToken cancellationToken);

    Task<CheckpointSaveResult> SaveAsync(CheckpointPayload payload, CancellationToken cancellationToken);
}
