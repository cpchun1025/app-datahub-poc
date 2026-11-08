using DataHub.Api.Persistence;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace DataHub.Api.Tests.Support;

internal sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Production;

    public string ApplicationName { get; set; } = "DataHub.Api.Tests";

    public string ContentRootPath { get; set; } = contentRootPath;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>記憶體 checkpoint store：可以指定載入結果、令保存失敗、或者令保存停喺途中。</summary>
internal sealed class FakeCheckpointStore : ICheckpointStore
{
    private readonly List<CheckpointPayload> _saved = [];
    private readonly TaskCompletionSource _saveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CheckpointLoadResult LoadResult { get; set; } = new(null, null, []);

    public Exception? LoadFailure { get; set; }

    public int LoadCalls { get; private set; }

    public Exception? FailWith { get; set; }

    public TaskCompletionSource? SaveGate { get; set; }

    public Task SaveStarted => _saveStarted.Task;

    public IReadOnlyList<CheckpointPayload> Saved
    {
        get
        {
            lock (_saved)
            {
                return [.. _saved];
            }
        }
    }

    public Task<CheckpointLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        LoadCalls++;
        return LoadFailure is { } failure ? Task.FromException<CheckpointLoadResult>(failure) : Task.FromResult(LoadResult);
    }

    public async Task<CheckpointSaveResult> SaveAsync(CheckpointPayload payload, CancellationToken cancellationToken)
    {
        _saveStarted.TrySetResult();

        if (SaveGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (FailWith is { } failure)
        {
            throw failure;
        }

        lock (_saved)
        {
            _saved.Add(payload);
        }

        return new CheckpointSaveResult(1_234, payload.RecordCount, TimeSpan.FromMilliseconds(3));
    }
}
