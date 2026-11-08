using DataHub.Api.Domain;
using DataHub.Api.Store;
using static DataHub.Api.Tests.Support.TestData;

namespace DataHub.Api.Tests.Store;

public class LatestStateStoreTests
{
    private static readonly DateTimeOffset Received = T0.AddMinutes(1);

    private static LatestStateStore NewStore(params string[] sourceIds)
        => new(sourceIds.Length == 0 ? ["src-a"] : sourceIds);

    [Fact]
    public void LatestValueWins_PriceGoingBackToTheOriginalValue_100_101_100()
    {
        var store = NewStore();

        store.ApplyUpdates("src-a", [Update("AAA", 100m, 1)], Received);
        store.ApplyUpdates("src-a", [Update("AAA", 101m, 2)], Received);
        var third = store.ApplyUpdates("src-a", [Update("AAA", 100m, 3)], Received);

        // 即使價格同第一次一樣，都係新嘅觀察值，所以要套用。
        Assert.Equal(1, third.Applied);
        var record = store.Find("src-a", "AAA");
        Assert.NotNull(record);
        Assert.Equal(100m, record.Price);
        Assert.Equal(3, record.SourceRevision);
    }

    [Fact]
    public void OlderOrDuplicateRevisionsAreIgnored()
    {
        var store = NewStore();
        store.ApplyUpdates("src-a", [Update("AAA", 105m, 5)], Received);

        var result = store.ApplyUpdates(
            "src-a",
            [Update("AAA", 104m, 4), Update("AAA", 999m, 5), Update("AAA", 103m, 3)],
            Received);

        Assert.Equal(0, result.Applied);
        Assert.Equal(3, result.Ignored);
        Assert.Equal(105m, store.Find("src-a", "AAA")!.Price);
    }

    [Fact]
    public void OutOfOrderUpdatesInOneBatchEndWithTheHighestRevision()
    {
        var store = NewStore();

        store.ApplyUpdates("src-a", [Update("AAA", 12m, 12), Update("AAA", 11m, 11), Update("AAA", 13m, 13)], Received);

        Assert.Equal(13m, store.Find("src-a", "AAA")!.Price);
    }

    [Fact]
    public void SameInstrumentIdFromDifferentSourcesDoesNotOverwrite()
    {
        var store = NewStore("src-a", "src-b");

        store.ApplyUpdates("src-a", [Update("AAA", 100m, 1, "USD")], Received);
        store.ApplyUpdates("src-b", [Update("AAA", 780m, 1, "HKD")], Received);
        store.ApplyUpdates("src-a", [Update("AAA", 101m, 2, "USD")], Received);

        var a = store.Find("src-a", "AAA");
        var b = store.Find("src-b", "AAA");
        Assert.Equal((101m, "USD", "src-a"), (a!.Price, a.Currency, a.SourceId));
        Assert.Equal((780m, "HKD", "src-b"), (b!.Price, b.Currency, b.SourceId));

        var all = store.Query(new PriceQuery(null, "AAA", 1, 10));
        Assert.Equal(2, all.TotalCount);
        Assert.Equal(["src-a", "src-b"], all.Items.Select(item => item.SourceId));
    }

    [Fact]
    public void RevisionsOfDifferentSourcesAreNeverCompared()
    {
        var store = NewStore("src-a", "src-b");
        store.ApplyUpdates("src-a", [Update("AAA", 1m, 1_000)], Received);

        // src-b 嘅 revision 細好多，但係佢屬於另一個來源，唔可以被 src-a 嘅 revision 擋住。
        var result = store.ApplyUpdates("src-b", [Update("AAA", 2m, 1)], Received);

        Assert.Equal(1, result.Applied);
        Assert.Equal(2m, store.Find("src-b", "AAA")!.Price);
    }

    [Fact]
    public void UnknownSourceIsRejected()
    {
        var store = NewStore();

        Assert.Throws<ArgumentException>(() => store.ApplyUpdates("nope", [Update("AAA", 1m, 1)], Received));
        Assert.Null(store.GetSource("nope"));
        Assert.Null(store.Find("nope", "AAA"));
    }

    [Fact]
    public void SourceLookupIsCaseInsensitiveButStoredIdKeepsItsCanonicalCasing()
    {
        var store = NewStore("Src-A");

        store.ApplyUpdates("SRC-A", [Update("AAA", 1m, 1)], Received);

        var record = store.Find("src-a", "AAA");
        Assert.NotNull(record);
        Assert.Equal("Src-A", record.SourceId);
    }

    [Fact]
    public void SnapshotMerge_AddsUpdatesAndRemovesMembers()
    {
        var store = NewStore();
        store.ApplySnapshot(
            "src-a",
            Snapshot(10, Update("A", 1m, 5), Update("B", 2m, 6), Update("C", 3m, 7)),
            Received,
            SnapshotApplyMode.Replace);

        var result = store.ApplySnapshot(
            "src-a",
            Snapshot(20, Update("A", 1.5m, 15), Update("C", 3m, 7), Update("D", 4m, 18)),
            Received,
            SnapshotApplyMode.Merge);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.KeptNewer);
        Assert.Equal(3, result.TotalRecords);
        Assert.False(result.Skipped);
        Assert.Equal(["A", "C", "D"], store.GetSource("src-a")!.Records.Keys);
        Assert.Equal(1.5m, store.Find("src-a", "A")!.Price);
        Assert.Equal(20, store.GetSource("src-a")!.SnapshotRevision);
    }

    [Fact]
    public void SnapshotMerge_DoesNotOverwriteNewerUpdates()
    {
        var store = NewStore();
        store.ApplySnapshot("src-a", Snapshot(10, Update("A", 100m, 5)), Received, SnapshotApplyMode.Replace);

        // Snapshot 喺 revision 11 切出，但傳輸期間 stream 已經送咗 revision 12。
        store.ApplyUpdates("src-a", [Update("A", 102m, 12)], Received);
        var result = store.ApplySnapshot("src-a", Snapshot(11, Update("A", 101m, 11)), Received, SnapshotApplyMode.Merge);

        Assert.Equal(1, result.KeptNewer);
        Assert.Equal(0, result.Updated);
        var record = store.Find("src-a", "A");
        Assert.Equal(102m, record!.Price);
        Assert.Equal(12, record.SourceRevision);
    }

    [Fact]
    public void SnapshotMerge_KeepsInstrumentsListedAfterTheSnapshotWasCut()
    {
        var store = NewStore();
        store.ApplySnapshot("src-a", Snapshot(10, Update("A", 1m, 5)), Received, SnapshotApplyMode.Replace);

        // X 喺 snapshot 切出（revision 20）之後先上市，所以 snapshot 冇 X 係正常嘅，唔可以當成已下市。
        store.ApplyUpdates("src-a", [Update("X", 9m, 25)], Received);
        var result = store.ApplySnapshot("src-a", Snapshot(20, Update("A", 1m, 5)), Received, SnapshotApplyMode.Merge);

        Assert.Equal(0, result.Removed);
        Assert.NotNull(store.Find("src-a", "X"));
    }

    [Fact]
    public void SnapshotMerge_OlderThanTheAppliedSnapshotIsSkipped()
    {
        var store = NewStore();
        store.ApplySnapshot("src-a", Snapshot(100, Update("A", 100m, 90)), Received, SnapshotApplyMode.Replace);
        var versionBefore = store.Version;

        var result = store.ApplySnapshot("src-a", Snapshot(50, Update("A", 50m, 50), Update("Z", 1m, 40)), Received, SnapshotApplyMode.Merge);

        Assert.True(result.Skipped);
        Assert.Equal(versionBefore, store.Version);
        Assert.Equal(100m, store.Find("src-a", "A")!.Price);
        Assert.Null(store.Find("src-a", "Z"));
    }

    [Fact]
    public void LateOldUpdateCannotResurrectAnInstrumentRemovedBySnapshot()
    {
        var store = NewStore();
        store.ApplySnapshot("src-a", Snapshot(10, Update("A", 1m, 5), Update("B", 2m, 6)), Received, SnapshotApplyMode.Replace);
        store.ApplySnapshot("src-a", Snapshot(20, Update("A", 1m, 5)), Received, SnapshotApplyMode.Merge);
        Assert.Null(store.Find("src-a", "B"));

        // revision 8 喺 snapshot 切點（20）之前，係遲到嘅舊訊息。
        var late = store.ApplyUpdates("src-a", [Update("B", 2.5m, 8)], Received);
        Assert.Equal(0, late.Applied);
        Assert.Null(store.Find("src-a", "B"));

        // revision 21 喺切點之後，B 重新上市，要接受。
        var relisted = store.ApplyUpdates("src-a", [Update("B", 3m, 21)], Received);
        Assert.Equal(1, relisted.Applied);
        Assert.Equal(3m, store.Find("src-a", "B")!.Price);
    }

    [Fact]
    public void SnapshotReplace_DiscardsRevisionsFromAnOlderSession()
    {
        var store = NewStore();
        store.Restore(SourceData.FromRecords(
            "src-a",
            [Record("src-a", "A", 999m, 500), Record("src-a", "B", 888m, 400)],
            snapshotRevision: 600,
            lastSnapshotAtUtc: T0,
            lastUpdateReceivedAtUtc: T0));

        // 新 session 嘅 revision 由 1 開始。Merge 會誤以為恢復嚟嘅 revision 500 更新，所以必須用 Replace。
        var result = store.ApplySnapshot("src-a", Snapshot(1, Update("A", 100m, 1)), Received, SnapshotApplyMode.Replace);

        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Updated);
        Assert.Equal(100m, store.Find("src-a", "A")!.Price);
        Assert.Null(store.Find("src-a", "B"));
        Assert.Equal(1, store.GetSource("src-a")!.SnapshotRevision);
    }

    [Fact]
    public void Snapshot_DuplicateInstrumentKeepsTheHighestRevision()
    {
        var store = NewStore();

        store.ApplySnapshot(
            "src-a",
            Snapshot(10, Update("A", 30m, 3), Update("A", 50m, 5), Update("A", 40m, 4)),
            Received,
            SnapshotApplyMode.Replace);

        Assert.Equal(50m, store.Find("src-a", "A")!.Price);
        Assert.Equal(1, store.GetSource("src-a")!.RecordCount);
    }

    [Fact]
    public void Query_PaginatesAcrossSourcesInAStableOrder()
    {
        var store = NewStore("src-b", "src-a");
        store.ApplyUpdates("src-a", [Update("A3", 3m, 3), Update("A1", 1m, 1), Update("A2", 2m, 2)], Received);
        store.ApplyUpdates("src-b", [Update("B2", 2m, 2), Update("B1", 1m, 1)], Received);

        var page1 = store.Query(new PriceQuery(null, null, 1, 2));
        var page2 = store.Query(new PriceQuery(null, null, 2, 2));
        var page3 = store.Query(new PriceQuery(null, null, 3, 2));
        var beyond = store.Query(new PriceQuery(null, null, 4, 2));

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(["A1", "A2"], page1.Items.Select(item => item.InstrumentId));
        Assert.Equal(["A3", "B1"], page2.Items.Select(item => item.InstrumentId));
        Assert.Equal(["B2"], page3.Items.Select(item => item.InstrumentId));
        Assert.Empty(beyond.Items);
        Assert.Equal(5, beyond.TotalCount);
    }

    [Fact]
    public void Query_FiltersBySourceAndByCaseInsensitiveInstrumentSearch()
    {
        var store = NewStore("src-a", "src-b");
        store.ApplyUpdates("src-a", [Update("AAPL", 1m, 1), Update("MSFT", 2m, 2), Update("aapl.hk", 3m, 3)], Received);
        store.ApplyUpdates("src-b", [Update("AAPL", 4m, 1)], Received);

        var everywhere = store.Query(new PriceQuery(null, "aapl", 1, 10));
        var onlyA = store.Query(new PriceQuery("src-a", "AAPL", 1, 10));
        var paged = store.Query(new PriceQuery(null, "aapl", 2, 2));

        Assert.Equal(3, everywhere.TotalCount);
        Assert.Equal(2, onlyA.TotalCount);
        Assert.All(onlyA.Items, item => Assert.Equal("src-a", item.SourceId));
        Assert.Equal(3, paged.TotalCount);
        Assert.Single(paged.Items);
    }

    [Fact]
    public void Query_UnknownSourceReturnsNothing()
    {
        var store = NewStore();
        store.ApplyUpdates("src-a", [Update("A", 1m, 1)], Received);

        var result = store.Query(new PriceQuery("nope", null, 1, 10));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public void Version_ChangesOnlyWhenTheDataChanges()
    {
        var store = NewStore();
        Assert.Equal(0, store.Version);

        store.ApplyUpdates("src-a", [Update("A", 1m, 1)], Received);
        var afterUpdate = store.Version;
        Assert.True(afterUpdate > 0);

        store.ApplyUpdates("src-a", [Update("A", 1m, 1)], Received);
        Assert.Equal(afterUpdate, store.Version);

        store.ApplySnapshot("src-a", Snapshot(5, Update("A", 1m, 1)), Received, SnapshotApplyMode.Replace);
        var afterSnapshot = store.Version;
        Assert.True(afterSnapshot > afterUpdate);

        store.Restore(SourceData.Empty("src-a"));
        Assert.True(store.Version > afterSnapshot);
    }

    [Fact]
    public void Capture_IsAStableViewThatLaterWritesDoNotChange()
    {
        var store = NewStore();
        store.ApplyUpdates("src-a", [Update("A", 1m, 1)], Received);

        var capture = store.Capture();
        store.ApplyUpdates("src-a", [Update("A", 2m, 2), Update("B", 3m, 3)], Received);

        var captured = Assert.Single(capture.Sources);
        Assert.Equal(1, captured.RecordCount);
        Assert.Equal(1m, captured.Records["A"].Price);
        Assert.True(store.Version > capture.Version);
    }

    [Fact]
    public async Task ConcurrentWritersAndReadersStayConsistent()
    {
        var sources = new[] { "src-a", "src-b", "src-c" };
        var store = NewStore(sources);
        const int instruments = 50;
        const int writes = 3_000;
        var writersDone = false;

        var writers = sources.Select(source => Task.Run(() =>
        {
            for (var revision = 1; revision <= writes; revision++)
            {
                store.ApplyUpdates(source, [Update($"I{revision % instruments:D2}", revision, revision)], Received);
            }
        })).ToArray();

        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref writersDone))
            {
                var capture = store.Capture();
                Assert.All(capture.Sources, source => Assert.True(source.RecordCount <= instruments));
                var page = store.Query(new PriceQuery(null, null, 1, 100));
                Assert.True(page.TotalCount <= instruments * sources.Length);
            }
        });

        await Task.WhenAll(writers);
        Volatile.Write(ref writersDone, true);
        await reader;

        foreach (var source in sources)
        {
            Assert.Equal(instruments, store.GetSource(source)!.RecordCount);

            // 每個 instrument 最終都應該係佢最後一次寫入嘅 revision。
            for (var k = 0; k < instruments; k++)
            {
                var lastRevision = Enumerable.Range(1, writes).Last(r => r % instruments == k);
                Assert.Equal(lastRevision, store.Find(source, $"I{k:D2}")!.SourceRevision);
            }
        }
    }
}
