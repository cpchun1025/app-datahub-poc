using System.Net;
using DataHub.Api.Domain;
using DataHub.Api.Persistence;
using DataHub.Api.Tests.Support;

namespace DataHub.Api.Tests.Api;

public class ApiCheckpointTests
{
    [Fact]
    public async Task ManualCheckpoint_WritesTheFile_RotatesTheBackup_AndIsReportedByHealth()
    {
        await using var scenario = new ApiScenario();
        var client = await scenario.StartAsync();

        var first = (await client.PostReplyAsync("/api/admin/checkpoint"))
            .Expect(HttpStatusCode.OK)
            .Json<DataHub.Api.Endpoints.CheckpointRunResponse>();
        Assert.Equal("Saved", first.Outcome);
        Assert.Equal(4, first.Checkpoint.LastSuccessRecordCount);
        Assert.True(File.Exists(scenario.CheckpointFile));
        Assert.False(File.Exists(scenario.CheckpointFile + ".bak"));

        var payload = CheckpointSerializer.Deserialize(await File.ReadAllBytesAsync(scenario.CheckpointFile));
        Assert.Equal(["alpha", "beta"], payload.Sources.Select(source => source.SourceId).Order());
        Assert.Equal(4, payload.RecordCount);

        // 第二次保存先會將上一份有效 checkpoint 輪替成 .bak。
        (await client.PostReplyAsync("/api/admin/checkpoint")).Expect(HttpStatusCode.OK);
        Assert.True(File.Exists(scenario.CheckpointFile + ".bak"));
        Assert.False(File.Exists(scenario.CheckpointFile + ".tmp"));

        var health = await client.GetHealthAsync();
        Assert.NotNull(health.Checkpoint.LastSuccessAtUtc);
        Assert.Equal(4, health.Checkpoint.LastSuccessRecordCount);
        Assert.Null(health.Checkpoint.LastError);
        Assert.Equal(0, health.Checkpoint.ConsecutiveFailures);
    }

    [Fact]
    public async Task CheckpointFailure_KeepsServing_AndReportsASanitizedError()
    {
        using var blocker = new TempDirectory();
        var notADirectory = blocker.File("not-a-directory");
        await File.WriteAllTextAsync(notADirectory, "a file, so no directory can be created below it");
        var settings = new Dictionary<string, string?>
        {
            ["DataHub:Checkpoint:Directory"] = Path.Combine(notADirectory, "checkpoints"),
        };

        await using var scenario = new ApiScenario(settings);
        var client = await scenario.StartAsync();

        var problem = (await client.PostReplyAsync("/api/admin/checkpoint")).ExpectProblem(HttpStatusCode.InternalServerError);
        Assert.Equal("Checkpoint save failed", problem.Title);
        Assert.DoesNotContain(blocker.FullPath, problem.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(4, (await client.GetPricesAsync()).TotalCount);

        var health = await client.GetHealthAsync();
        Assert.Equal("ok", health.Status);
        Assert.NotNull(health.Checkpoint.LastError);
        Assert.DoesNotContain(blocker.FullPath, health.Checkpoint.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(health.Checkpoint.LastErrorAtUtc);
        Assert.Equal(1, health.Checkpoint.ConsecutiveFailures);
        Assert.Null(health.Checkpoint.LastSuccessAtUtc);
    }

    [Fact]
    public async Task CheckpointDisabled_ReturnsConflict_AndNothingIsWritten()
    {
        var settings = new Dictionary<string, string?> { ["DataHub:Checkpoint:Enabled"] = "false" };
        await using var scenario = new ApiScenario(settings);
        var client = await scenario.StartAsync();

        var problem = (await client.PostReplyAsync("/api/admin/checkpoint")).ExpectProblem(HttpStatusCode.Conflict);

        Assert.Equal("Checkpoint disabled", problem.Title);
        Assert.False((await client.GetHealthAsync()).Checkpoint.Enabled);
        Assert.False(File.Exists(scenario.CheckpointFile));
    }
}

public class ApiRecoveryTests
{
    [Fact]
    public async Task GracefulShutdown_WritesACheckpoint_AndTheNextStartServesItAsRecoveringUntilResynced()
    {
        using var checkpoints = new TempDirectory();
        var saveOnShutdown = new Dictionary<string, string?> { ["DataHub:Checkpoint:SaveOnShutdown"] = "true" };

        await using (var first = new ApiScenario(saveOnShutdown, checkpoints))
        {
            await first.StartAsync();
            Assert.False(File.Exists(first.CheckpointFile));
        }

        Assert.True(File.Exists(checkpoints.File("checkpoint.json")));

        await using var second = new ApiScenario(checkpoints: checkpoints);

        // 停機期間來源嘅世界已經變咗：AAA 下市、新增 NEW。Snapshot 扣住，先睇到「已恢復但未同步」嘅狀態。
        second.Alpha.Upstream.Remove("AAA");
        second.Alpha.Upstream.Set("NEW", 5m);
        second.Alpha.HoldSnapshots();
        second.Beta.HoldSnapshots();

        var client = second.Client;

        var sources = (await client.GetSourcesAsync()).Sources;
        Assert.All(sources, source =>
        {
            Assert.Equal(SourceStatus.Recovering, source.Status);
            Assert.True(source.RestoredFromCheckpoint);
            Assert.False(source.SyncedSinceStart);
            Assert.Equal(2, source.RecordCount);
        });

        var restored = await client.GetPricesAsync();
        Assert.Equal(4, restored.TotalCount);
        Assert.All(restored.Items, item => Assert.Equal(SourceStatus.Recovering, item.SourceStatus));
        Assert.Contains(restored.Items, item => item is { SourceId: "alpha", InstrumentId: "AAA" });

        var health = await client.GetHealthAsync();
        Assert.Equal((2, 0), (health.Sources.Recovering, health.Sources.Live));
        Assert.Equal("primary", health.Checkpoint.LoadedFrom);
        Assert.Equal(4, health.Checkpoint.LoadedRecordCount);
        Assert.Empty(health.Checkpoint.LoadWarnings);

        // Snapshot 已經開始（切點已定）但未完成：仍然唔可以係 Live。
        await ApiExtensions.WaitForAsync(
            () => Task.FromResult((Alpha: second.Alpha.GatedSnapshotCalls, Beta: second.Beta.GatedSnapshotCalls)),
            calls => calls is { Alpha: >= 1, Beta: >= 1 },
            "both initial snapshots to be in flight");
        Assert.All((await client.GetSourcesAsync()).Sources, source => Assert.Equal(SourceStatus.Recovering, source.Status));

        await ReleaseAndWaitForLiveAsync(second);

        var synced = await client.GetPricesAsync();
        Assert.Equal(
            ["alpha/NEW", "alpha/SHARED", "beta/BBB", "beta/SHARED"],
            synced.Items.Select(item => $"{item.SourceId}/{item.InstrumentId}"));
        Assert.All(synced.Items, item => Assert.Equal(SourceStatus.Live, item.SourceStatus));
    }

    [Fact]
    public async Task BackupCheckpoint_IsUsedWhenThePrimaryIsCorrupt()
    {
        using var checkpoints = new TempDirectory();

        await using (var first = new ApiScenario(checkpoints: checkpoints))
        {
            var client = await first.StartAsync();
            (await client.PostReplyAsync("/api/admin/checkpoint")).Expect(HttpStatusCode.OK);
            (await client.PostReplyAsync("/api/admin/checkpoint")).Expect(HttpStatusCode.OK);
        }

        Assert.True(File.Exists(checkpoints.File("checkpoint.json.bak")));
        await File.WriteAllTextAsync(checkpoints.File("checkpoint.json"), "{ truncated");

        await using var second = new ApiScenario(checkpoints: checkpoints);
        second.Alpha.HoldSnapshots();
        second.Beta.HoldSnapshots();
        var restoredClient = second.Client;

        var health = await restoredClient.GetHealthAsync();
        Assert.Equal("backup", health.Checkpoint.LoadedFrom);
        Assert.Equal(4, health.Checkpoint.LoadedRecordCount);
        var warning = Assert.Single(health.Checkpoint.LoadWarnings);
        Assert.Contains("primary", warning);
        Assert.DoesNotContain(checkpoints.FullPath, warning, StringComparison.OrdinalIgnoreCase);

        Assert.All((await restoredClient.GetSourcesAsync()).Sources, source => Assert.Equal(SourceStatus.Recovering, source.Status));
        Assert.Equal(4, (await restoredClient.GetPricesAsync()).TotalCount);

        await ReleaseAndWaitForLiveAsync(second);
    }

    [Fact]
    public async Task CorruptCheckpoint_NeverBlocksStartup_AndTheNextSaveRepairsIt()
    {
        using var checkpoints = new TempDirectory();
        await File.WriteAllTextAsync(checkpoints.File("checkpoint.json"), "{ this is not a checkpoint");

        await using var scenario = new ApiScenario(checkpoints: checkpoints);
        var client = await scenario.StartAsync();

        var health = await client.GetHealthAsync();
        Assert.Equal("ok", health.Status);
        Assert.Null(health.Checkpoint.LoadedFrom);
        var warning = Assert.Single(health.Checkpoint.LoadWarnings);
        Assert.Contains("checkpoint.json", warning);
        Assert.DoesNotContain(checkpoints.FullPath, warning, StringComparison.OrdinalIgnoreCase);
        Assert.All((await client.GetSourcesAsync()).Sources, source => Assert.False(source.RestoredFromCheckpoint));

        (await client.PostReplyAsync("/api/admin/checkpoint")).Expect(HttpStatusCode.OK);
        var repaired = CheckpointSerializer.Deserialize(await File.ReadAllBytesAsync(scenario.CheckpointFile));
        Assert.Equal(4, repaired.RecordCount);

        // 損壞嘅 primary 唔可以被輪替成 .bak，否則會蓋走最後一份好嘅 checkpoint。
        Assert.False(File.Exists(scenario.CheckpointFile + ".bak"));
    }

    private static async Task ReleaseAndWaitForLiveAsync(ApiScenario scenario)
    {
        scenario.Alpha.ReleaseSnapshots();
        scenario.Beta.ReleaseSnapshots();
        await scenario.Client.WaitForLiveAsync("alpha", "beta");
    }
}
