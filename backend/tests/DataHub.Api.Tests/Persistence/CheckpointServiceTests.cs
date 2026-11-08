using DataHub.Api.Configuration;
using DataHub.Api.Persistence;
using DataHub.Api.Store;
using DataHub.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static DataHub.Api.Tests.Support.TestData;

namespace DataHub.Api.Tests.Persistence;

public class CheckpointServiceTests
{
    private readonly FakeTimeProvider _time = new(T0);
    private readonly LatestStateStore _store = new(["src-a"]);
    private readonly FakeCheckpointStore _files = new();

    private CheckpointService NewService(Action<DataHubOptions>? configure = null)
        => new(
            _store,
            _files,
            Options.Create(NewOptions(configure)),
            _time,
            NullLogger<CheckpointService>.Instance);

    private void Seed(decimal price = 1m, long revision = 1)
        => _store.ApplyUpdates("src-a", [Update("A", price, revision)], T0);

    [Fact]
    public async Task NothingLoadedOrSynced_IsNeverWritten()
    {
        var service = NewService();

        var result = await service.SaveAsync(CheckpointTrigger.Manual, CancellationToken.None);

        Assert.Equal(CheckpointOutcome.SkippedEmpty, result.Outcome);
        Assert.Empty(_files.Saved);
    }

    [Fact]
    public async Task ASourceThatSyncedWithZeroRecords_StillCountsAsData()
    {
        var service = NewService();
        _store.ApplySnapshot("src-a", Snapshot(5), T0, SnapshotApplyMode.Replace);

        var result = await service.SaveAsync(CheckpointTrigger.Periodic, CancellationToken.None);

        Assert.Equal(CheckpointOutcome.Saved, result.Outcome);
    }

    [Fact]
    public async Task Saves_ThenSkipsWhenUnchanged_ThenSavesAgainAfterAChange()
    {
        var service = NewService();
        Seed();

        var first = await service.SaveAsync(CheckpointTrigger.Periodic, CancellationToken.None);
        var unchanged = await service.SaveAsync(CheckpointTrigger.Periodic, CancellationToken.None);
        Seed(2m, 2);
        var changed = await service.SaveAsync(CheckpointTrigger.Periodic, CancellationToken.None);

        Assert.Equal(CheckpointOutcome.Saved, first.Outcome);
        Assert.Equal(CheckpointOutcome.SkippedUnchanged, unchanged.Outcome);
        Assert.Equal(CheckpointOutcome.Saved, changed.Outcome);
        Assert.Equal(2, _files.Saved.Count);
        Assert.Equal(2m, _files.Saved[1].Sources.Single().Records["A"].Price);
    }

    [Fact]
    public async Task ManualSave_AlwaysWrites_EvenWhenNothingChanged()
    {
        var service = NewService();
        Seed();
        await service.SaveAsync(CheckpointTrigger.Periodic, CancellationToken.None);

        var manual = await service.SaveAsync(CheckpointTrigger.Manual, CancellationToken.None);

        Assert.Equal(CheckpointOutcome.Saved, manual.Outcome);
        Assert.Equal(2, _files.Saved.Count);
    }

    [Fact]
    public async Task SuccessfulSave_RecordsTheStatusFromTheStoreCapture()
    {
        var service = NewService();
        Seed();
        _time.Advance(TimeSpan.FromSeconds(30));

        var result = await service.SaveAsync(CheckpointTrigger.Manual, CancellationToken.None);

        Assert.Equal(CheckpointOutcome.Saved, result.Outcome);
        var status = service.Status;
        Assert.True(status.Enabled);
        Assert.False(status.SaveInProgress);
        Assert.Equal(T0.AddSeconds(30), status.LastSuccessAtUtc);
        Assert.Equal(1, status.LastSuccessRecordCount);
        Assert.Equal(1_234, status.LastSuccessBytes);
        Assert.Null(status.LastError);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Equal(T0.AddSeconds(30), _files.Saved.Single().SavedAtUtc);
    }

    [Fact]
    public async Task Failure_IsRecordedRetriedAndClearedByTheNextSuccess()
    {
        var service = NewService();
        Seed();
        _files.FailWith = new IOException("disk full");

        var first = await service.SaveAsync(CheckpointTrigger.Periodic, CancellationToken.None);

        Assert.Equal(CheckpointOutcome.Failed, first.Outcome);
        Assert.Equal("disk full", first.Error);
        Assert.Equal("disk full", service.Status.LastError);
        Assert.Equal(1, service.Status.ConsecutiveFailures);
        Assert.False(service.Status.SaveInProgress);
        Assert.NotNull(service.Status.LastErrorAtUtc);

        // 失敗冇令 store version 被當成「已保存」，所以下一次定時保存會重試，而唔係當成冇變動。
        var second = await service.SaveAsync(CheckpointTrigger.Periodic, CancellationToken.None);
        Assert.Equal(CheckpointOutcome.Failed, second.Outcome);
        Assert.Equal(2, service.Status.ConsecutiveFailures);

        _files.FailWith = null;
        var recovered = await service.SaveAsync(CheckpointTrigger.Periodic, CancellationToken.None);

        Assert.Equal(CheckpointOutcome.Saved, recovered.Outcome);
        Assert.Null(service.Status.LastError);
        Assert.Null(service.Status.LastErrorAtUtc);
        Assert.Equal(0, service.Status.ConsecutiveFailures);
    }

    [Fact]
    public async Task Disabled_NeverTouchesTheFileStore()
    {
        var service = NewService(o => o.Checkpoint.Enabled = false);
        Seed();

        var result = await service.SaveAsync(CheckpointTrigger.Manual, CancellationToken.None);

        Assert.Equal(CheckpointOutcome.Disabled, result.Outcome);
        Assert.False(service.Status.Enabled);
        Assert.Empty(_files.Saved);
    }

    [Fact]
    public async Task OverlappingSaveIsBusy_AndTheShutdownSaveWaitsForTheRunningOne()
    {
        var service = NewService();
        Seed();
        _files.SaveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = service.SaveAsync(CheckpointTrigger.Manual, CancellationToken.None);
        await _files.SaveStarted;
        Assert.True(service.Status.SaveInProgress);

        var overlapping = await service.SaveAsync(CheckpointTrigger.Periodic, CancellationToken.None);
        Assert.Equal(CheckpointOutcome.Busy, overlapping.Outcome);

        // 保存途中又有新資料到達，最後一次關機保存一定要包含佢。
        Seed(2m, 2);
        var shutdown = service.SaveAsync(CheckpointTrigger.Shutdown, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(shutdown.IsCompleted);

        _files.SaveGate.SetResult();

        Assert.Equal(CheckpointOutcome.Saved, (await running).Outcome);
        Assert.Equal(CheckpointOutcome.Saved, (await shutdown).Outcome);
        Assert.Equal(2, _files.Saved.Count);
        Assert.Equal(2m, _files.Saved[1].Sources.Single().Records["A"].Price);
    }

    [Fact]
    public async Task CancellationDuringASave_IsNotRecordedAsAFailure()
    {
        var service = NewService();
        Seed();
        _files.SaveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var running = service.SaveAsync(CheckpointTrigger.Shutdown, cts.Token);
        await _files.SaveStarted;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(0, service.Status.ConsecutiveFailures);
        Assert.False(service.Status.SaveInProgress);
    }

    [Fact]
    public async Task LoadedFromPrimary_DoesNotImmediatelyRewriteAnUnchangedCheckpoint()
    {
        var service = NewService();
        Seed();
        var payload = new CheckpointPayload(1, T0, _store.GetAllSources());

        service.OnLoaded(new CheckpointLoadResult(payload, "primary", []), _store.Version);
        var result = await service.SaveAsync(CheckpointTrigger.Periodic, CancellationToken.None);

        Assert.Equal(CheckpointOutcome.SkippedUnchanged, result.Outcome);
        Assert.Equal("primary", service.Status.LoadedFrom);
        Assert.Equal(1, service.Status.LoadedRecordCount);
        Assert.Equal(T0, service.Status.LoadedSavedAtUtc);
    }

    [Fact]
    public async Task LoadedFromBackup_SavesAgainSoThePrimaryIsRepaired()
    {
        var service = NewService();
        Seed();
        var payload = new CheckpointPayload(1, T0, _store.GetAllSources());

        service.OnLoaded(
            new CheckpointLoadResult(payload, "backup", ["primary checkpoint 'checkpoint.json' is unusable: corrupt"]),
            _store.Version);
        var result = await service.SaveAsync(CheckpointTrigger.Periodic, CancellationToken.None);

        Assert.Equal(CheckpointOutcome.Saved, result.Outcome);
        Assert.Equal("backup", service.Status.LoadedFrom);
        Assert.Single(service.Status.LoadWarnings);
    }

    [Fact]
    public void LoadedNothing_IsReportedAsSuch()
    {
        var service = NewService();

        service.OnLoaded(new CheckpointLoadResult(null, null, []), _store.Version);

        Assert.Null(service.Status.LoadedFrom);
        Assert.Null(service.Status.LoadedRecordCount);
        Assert.Empty(service.Status.LoadWarnings);
    }
}
