using DataHub.Api.Store;

namespace DataHub.Api.Persistence;

/// <summary>要保存／已恢復嘅內容。<see cref="Version"/> 係 capture 當刻 store 嘅版本。</summary>
public sealed record CheckpointPayload(
    long Version,
    DateTimeOffset SavedAtUtc,
    IReadOnlyList<SourceData> Sources)
{
    public long RecordCount => Sources.Sum(source => (long)source.RecordCount);
}

public sealed record CheckpointLoadResult(
    CheckpointPayload? Payload,
    string? LoadedFrom,
    IReadOnlyList<string> Warnings);

public sealed record CheckpointSaveResult(long Bytes, long RecordCount, TimeSpan Duration);

public sealed class CheckpointFormatException(string message, Exception? inner = null) : Exception(message, inner);

public interface ICheckpointStore
{
    /// <summary>載入 checkpoint：primary → backup → 無。任何損壞都只會變成 warning，唔會拋出例外令啟動失敗。</summary>
    Task<CheckpointLoadResult> LoadAsync(CancellationToken cancellationToken);

    Task<CheckpointSaveResult> SaveAsync(CheckpointPayload payload, CancellationToken cancellationToken);
}
