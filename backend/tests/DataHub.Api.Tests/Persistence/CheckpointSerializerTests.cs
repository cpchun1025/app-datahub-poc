using System.Security.Cryptography;
using System.Text;
using DataHub.Api.Persistence;
using DataHub.Api.Store;
using static DataHub.Api.Tests.Support.TestData;

namespace DataHub.Api.Tests.Persistence;

public class CheckpointSerializerTests
{
    private static CheckpointPayload SamplePayload()
    {
        var a = SourceData.FromRecords(
            "src-a",
            [
                Record("src-a", "AAA", 123.4567m, 7),
                Record("src-a", "BBB", 0.0001m, 9, "HKD"),
            ],
            snapshotRevision: 5,
            lastSnapshotAtUtc: T0.AddSeconds(5),
            lastUpdateReceivedAtUtc: T0.AddSeconds(9));
        var b = SourceData.FromRecords(
            "src-b",
            [Record("src-b", "AAA", 780.5m, 3, "HKD")],
            snapshotRevision: 3,
            lastSnapshotAtUtc: T0.AddSeconds(3),
            lastUpdateReceivedAtUtc: null);
        return new CheckpointPayload(42, T0.AddMinutes(10), [b, a]);
    }

    private static byte[] Envelope(
        string sourcesJson,
        int schemaVersion = 1,
        long recordCount = 0,
        string? hash = null)
    {
        hash ??= Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcesJson))).ToLowerInvariant();
        var json = $$"""
            {"schemaVersion":{{schemaVersion}},"checkpointVersion":1,"savedAtUtc":"2026-01-01T00:00:00.0000000Z","recordCount":{{recordCount}},"payloadSha256":"{{hash}}","sources":{{sourcesJson}}}
            """;
        return Encoding.UTF8.GetBytes(json);
    }

    private static string RecordJson(string id, string revision = "1")
        => $$"""{"instrumentId":"{{id}}","price":1.5,"currency":"USD","sourceUpdatedAtUtc":"2026-01-01T00:00:01Z","receivedAtUtc":"2026-01-01T00:00:02Z","sourceRevision":{{revision}}}""";

    [Fact]
    public void RoundTrip_PreservesEveryFieldExactly()
    {
        var original = SamplePayload();

        var restored = CheckpointSerializer.Deserialize(CheckpointSerializer.Serialize(original));

        Assert.Equal(original.Version, restored.Version);
        Assert.Equal(original.SavedAtUtc, restored.SavedAtUtc);
        Assert.Equal(3, restored.RecordCount);
        Assert.Equal(["src-a", "src-b"], restored.Sources.Select(source => source.SourceId));

        foreach (var expected in original.Sources)
        {
            var actual = restored.Sources.Single(source => source.SourceId == expected.SourceId);
            Assert.Equal(expected.SnapshotRevision, actual.SnapshotRevision);
            Assert.Equal(expected.LastSnapshotAtUtc, actual.LastSnapshotAtUtc);
            Assert.Equal(expected.LastUpdateReceivedAtUtc, actual.LastUpdateReceivedAtUtc);
            Assert.Equal(expected.Records.Values, actual.Records.Values);
        }
    }

    [Fact]
    public void RoundTrip_KeepsDecimalPrecisionAndUtcOffset()
    {
        var restored = CheckpointSerializer.Deserialize(CheckpointSerializer.Serialize(SamplePayload()));

        var record = restored.Sources.Single(source => source.SourceId == "src-a").Records["BBB"];
        Assert.Equal(0.0001m, record.Price);
        Assert.Equal("HKD", record.Currency);
        Assert.Equal(TimeSpan.Zero, record.SourceUpdatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, restored.SavedAtUtc.Offset);
    }

    [Fact]
    public void Serialize_IsDeterministicRegardlessOfSourceOrder()
    {
        var payload = SamplePayload();
        var reversed = payload with { Sources = [.. payload.Sources.Reverse()] };

        Assert.Equal(CheckpointSerializer.Serialize(payload), CheckpointSerializer.Serialize(reversed));
    }

    [Fact]
    public void RoundTrip_AllowsAnEmptyPayload()
    {
        var empty = new CheckpointPayload(1, T0, [SourceData.Empty("src-a")]);

        var restored = CheckpointSerializer.Deserialize(CheckpointSerializer.Serialize(empty));

        Assert.Equal(0, restored.RecordCount);
        Assert.Single(restored.Sources);
    }

    [Fact]
    public void Deserialize_RejectsEmptyAndGarbageInput()
    {
        Assert.Throws<CheckpointFormatException>(() => CheckpointSerializer.Deserialize([]));
        Assert.Throws<CheckpointFormatException>(() => CheckpointSerializer.Deserialize("not json at all"u8));
        Assert.Throws<CheckpointFormatException>(() => CheckpointSerializer.Deserialize("[]"u8));
        Assert.Throws<CheckpointFormatException>(() => CheckpointSerializer.Deserialize("{}"u8));
    }

    [Fact]
    public void Deserialize_RejectsATruncatedFile()
    {
        var bytes = CheckpointSerializer.Serialize(SamplePayload());

        Assert.Throws<CheckpointFormatException>(() => CheckpointSerializer.Deserialize(bytes.AsSpan(0, bytes.Length / 2)));
    }

    [Fact]
    public void Deserialize_RejectsATamperedPayloadByChecksum()
    {
        var text = Encoding.UTF8.GetString(CheckpointSerializer.Serialize(SamplePayload()));
        var tampered = text.Replace("123.4567", "999.4567", StringComparison.Ordinal);
        Assert.NotEqual(text, tampered);

        var exception = Assert.Throws<CheckpointFormatException>(
            () => CheckpointSerializer.Deserialize(Encoding.UTF8.GetBytes(tampered)));

        Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_RejectsAnUnsupportedSchemaVersion()
    {
        var bytes = Envelope("[]", schemaVersion: 2);

        var exception = Assert.Throws<CheckpointFormatException>(() => CheckpointSerializer.Deserialize(bytes));

        Assert.Contains("schemaVersion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_AcceptsAHandBuiltValidEnvelope()
    {
        var sources = $$"""[{"sourceId":"src-a","snapshotRevision":4,"records":[{{RecordJson("AAA")}}]}]""";

        var payload = CheckpointSerializer.Deserialize(Envelope(sources, recordCount: 1));

        var source = Assert.Single(payload.Sources);
        Assert.Equal("src-a", source.SourceId);
        Assert.Equal(1.5m, source.Records["AAA"].Price);
        Assert.Null(source.LastSnapshotAtUtc);
    }

    [Fact]
    public void Deserialize_RejectsDuplicateSourceIds()
    {
        var sources = """[{"sourceId":"src-a","records":[]},{"sourceId":"SRC-A","records":[]}]""";

        Assert.Throws<CheckpointFormatException>(() => CheckpointSerializer.Deserialize(Envelope(sources)));
    }

    [Fact]
    public void Deserialize_RejectsDuplicateInstrumentsWithinOneSource()
    {
        var sources = $$"""[{"sourceId":"src-a","records":[{{RecordJson("AAA", "1")}},{{RecordJson("AAA", "2")}}]}]""";

        var exception = Assert.Throws<CheckpointFormatException>(
            () => CheckpointSerializer.Deserialize(Envelope(sources, recordCount: 2)));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_RejectsARecordWithoutInstrumentId()
    {
        var sources = """[{"sourceId":"src-a","records":[{"price":1,"currency":"USD","sourceRevision":1}]}]""";

        Assert.Throws<CheckpointFormatException>(() => CheckpointSerializer.Deserialize(Envelope(sources, recordCount: 1)));
    }

    [Fact]
    public void Deserialize_RejectsAMismatchedRecordCount()
    {
        var sources = $$"""[{"sourceId":"src-a","records":[{{RecordJson("AAA")}}]}]""";

        var exception = Assert.Throws<CheckpointFormatException>(
            () => CheckpointSerializer.Deserialize(Envelope(sources, recordCount: 5)));

        Assert.Contains("recordCount", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_RejectsNullSourcesAndNullRecords()
    {
        Assert.Throws<CheckpointFormatException>(() => CheckpointSerializer.Deserialize(Envelope("null")));
        Assert.Throws<CheckpointFormatException>(
            () => CheckpointSerializer.Deserialize(Envelope("""[{"sourceId":"src-a","records":null}]""")));
    }

    [Fact]
    public void Deserialize_RejectsAWrongTypedField()
    {
        // 結構完整、checksum 正確，但欄位型別錯，要變成 CheckpointFormatException（唔係未處理嘅其他例外）。
        var sources = """[{"sourceId":"src-a","records":[{"instrumentId":"AAA","price":"abc","sourceRevision":1}]}]""";

        Assert.Throws<CheckpointFormatException>(() => CheckpointSerializer.Deserialize(Envelope(sources, recordCount: 1)));
    }
}
