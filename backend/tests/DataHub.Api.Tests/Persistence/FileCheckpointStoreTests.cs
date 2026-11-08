using DataHub.Api.Persistence;
using DataHub.Api.Store;
using DataHub.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static DataHub.Api.Tests.Support.TestData;

namespace DataHub.Api.Tests.Persistence;

public sealed class FileCheckpointStoreTests : IDisposable
{
    private const string FileName = "checkpoint.json";

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Primary => _dir.File(FileName);

    private string Backup => _dir.File(FileName + ".bak");

    private string Temp => _dir.File(FileName + ".tmp");

    private FileCheckpointStore NewStore(string? directory = null)
    {
        var options = NewOptions(o => o.Checkpoint.Directory = directory ?? _dir.FullPath);
        return new FileCheckpointStore(
            Options.Create(options),
            new TestHostEnvironment(_dir.FullPath),
            NullLogger<FileCheckpointStore>.Instance);
    }

    private static CheckpointPayload Payload(long version, decimal price)
    {
        var data = SourceData.FromRecords(
            "src-a",
            [Record("src-a", "AAA", price, version)],
            snapshotRevision: version,
            lastSnapshotAtUtc: T0,
            lastUpdateReceivedAtUtc: T0);
        return new CheckpointPayload(version, T0.AddSeconds(version), [data]);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsThroughTheFileSystem()
    {
        var store = NewStore();

        var saved = await store.SaveAsync(Payload(1, 10.5m), CancellationToken.None);
        var load = await NewStore().LoadAsync(CancellationToken.None);

        Assert.Equal(1, saved.RecordCount);
        Assert.Equal(new FileInfo(Primary).Length, saved.Bytes);
        Assert.Equal("primary", load.LoadedFrom);
        Assert.Empty(load.Warnings);
        Assert.NotNull(load.Payload);
        Assert.Equal(10.5m, load.Payload.Sources.Single().Records["AAA"].Price);
    }

    [Fact]
    public async Task Save_LeavesNoTempFileAndNoBackupOnTheFirstSave()
    {
        await NewStore().SaveAsync(Payload(1, 1m), CancellationToken.None);

        Assert.True(File.Exists(Primary));
        Assert.False(File.Exists(Temp));
        Assert.False(File.Exists(Backup));
    }

    [Fact]
    public async Task SecondSave_RotatesThePreviousCheckpointToTheBackup()
    {
        var store = NewStore();
        await store.SaveAsync(Payload(1, 1m), CancellationToken.None);
        await store.SaveAsync(Payload(2, 2m), CancellationToken.None);

        Assert.True(File.Exists(Backup));
        Assert.Equal(2, CheckpointSerializer.Deserialize(await File.ReadAllBytesAsync(Primary)).Version);
        Assert.Equal(1, CheckpointSerializer.Deserialize(await File.ReadAllBytesAsync(Backup)).Version);
        Assert.False(File.Exists(Temp));
    }

    [Fact]
    public async Task CorruptPrimary_FallsBackToTheBackupWithAWarning()
    {
        var writer = NewStore();
        await writer.SaveAsync(Payload(1, 1m), CancellationToken.None);
        await writer.SaveAsync(Payload(2, 2m), CancellationToken.None);
        await File.WriteAllTextAsync(Primary, "{ this is not a checkpoint");

        var load = await NewStore().LoadAsync(CancellationToken.None);

        Assert.Equal("backup", load.LoadedFrom);
        Assert.NotNull(load.Payload);
        Assert.Equal(1, load.Payload.Version);
        var warning = Assert.Single(load.Warnings);
        Assert.Contains("primary", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatedPrimary_IsTreatedAsCorrupt()
    {
        var writer = NewStore();
        await writer.SaveAsync(Payload(1, 1m), CancellationToken.None);
        await writer.SaveAsync(Payload(2, 2m), CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(Primary);
        await File.WriteAllBytesAsync(Primary, bytes.AsMemory(0, bytes.Length - 20).ToArray());

        var load = await NewStore().LoadAsync(CancellationToken.None);

        Assert.Equal("backup", load.LoadedFrom);
        Assert.Equal(1, load.Payload!.Version);
    }

    [Fact]
    public async Task BothFilesCorrupt_YieldsNoPayloadButNeverThrows()
    {
        await File.WriteAllTextAsync(Primary, "garbage");
        await File.WriteAllTextAsync(Backup, "more garbage");

        var load = await NewStore().LoadAsync(CancellationToken.None);

        Assert.Null(load.Payload);
        Assert.Null(load.LoadedFrom);
        Assert.Equal(2, load.Warnings.Count);
    }

    [Fact]
    public async Task MissingCheckpoint_YieldsNothingWithoutWarnings()
    {
        var load = await NewStore().LoadAsync(CancellationToken.None);

        Assert.Null(load.Payload);
        Assert.Null(load.LoadedFrom);
        Assert.Empty(load.Warnings);
    }

    [Fact]
    public async Task MissingDirectory_IsNotAnErrorOnLoad()
    {
        var load = await NewStore(_dir.File("does-not-exist")).LoadAsync(CancellationToken.None);

        Assert.Null(load.Payload);
        Assert.Empty(load.Warnings);
    }

    [Fact]
    public async Task LeftoverTempFileFromACrashedSave_IsIgnored()
    {
        await NewStore().SaveAsync(Payload(1, 1m), CancellationToken.None);
        await File.WriteAllTextAsync(Temp, "half written");

        var load = await NewStore().LoadAsync(CancellationToken.None);

        Assert.Equal("primary", load.LoadedFrom);
        Assert.Empty(load.Warnings);
    }

    [Fact]
    public async Task LeftoverTempFile_IsOverwrittenByTheNextSave()
    {
        await File.WriteAllTextAsync(Temp, "half written");

        await NewStore().SaveAsync(Payload(1, 1m), CancellationToken.None);

        Assert.False(File.Exists(Temp));
        Assert.Equal(1, (await NewStore().LoadAsync(CancellationToken.None)).Payload!.Version);
    }

    [Fact]
    public async Task CorruptPrimary_IsNotRotatedOverTheGoodBackup_AfterALoad()
    {
        var first = NewStore();
        await first.SaveAsync(Payload(1, 1m), CancellationToken.None);
        await first.SaveAsync(Payload(2, 2m), CancellationToken.None);
        await File.WriteAllTextAsync(Primary, "corrupt");

        // 模擬重啟：載入時見到 primary 損壞，然後保存新一份。
        var restarted = NewStore();
        await restarted.LoadAsync(CancellationToken.None);
        await restarted.SaveAsync(Payload(3, 3m), CancellationToken.None);

        Assert.Equal(3, CheckpointSerializer.Deserialize(await File.ReadAllBytesAsync(Primary)).Version);
        Assert.Equal(1, CheckpointSerializer.Deserialize(await File.ReadAllBytesAsync(Backup)).Version);
    }

    [Fact]
    public async Task CorruptPrimary_IsNotRotatedOverTheGoodBackup_EvenWithoutALoad()
    {
        var first = NewStore();
        await first.SaveAsync(Payload(1, 1m), CancellationToken.None);
        await first.SaveAsync(Payload(2, 2m), CancellationToken.None);
        await File.WriteAllTextAsync(Primary, "corrupt");

        // 全新 instance 未載入過，保存前會自行驗證現有 primary。
        await NewStore().SaveAsync(Payload(3, 3m), CancellationToken.None);

        Assert.Equal(3, CheckpointSerializer.Deserialize(await File.ReadAllBytesAsync(Primary)).Version);
        Assert.Equal(1, CheckpointSerializer.Deserialize(await File.ReadAllBytesAsync(Backup)).Version);
    }

    [Fact]
    public async Task RelativeDirectory_ResolvesAgainstTheContentRoot()
    {
        var store = NewStore(Path.Combine("data", "cp"));

        await store.SaveAsync(Payload(1, 1m), CancellationToken.None);

        Assert.Equal(Path.GetFullPath(_dir.File(Path.Combine("data", "cp"))), store.Directory);
        Assert.True(File.Exists(_dir.File(Path.Combine("data", "cp", FileName))));
    }

    [Fact]
    public async Task SaveFailure_SurfacesASanitizedIOException()
    {
        var blocker = _dir.File("blocker");
        await File.WriteAllTextAsync(blocker, "I am a file, not a directory");
        var store = NewStore(Path.Combine(blocker, "sub"));

        var exception = await Assert.ThrowsAsync<IOException>(
            () => store.SaveAsync(Payload(1, 1m), CancellationToken.None));

        Assert.StartsWith("Checkpoint save failed", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_dir.FullPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public async Task SaveCancellation_PropagatesAsCancellationAndLeavesThePrimaryIntact()
    {
        var store = NewStore();
        await store.SaveAsync(Payload(1, 1m), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Payload(2, 2m), cts.Token));

        Assert.Equal(1, (await NewStore().LoadAsync(CancellationToken.None)).Payload!.Version);
    }
}
