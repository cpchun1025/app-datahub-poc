using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataHub.Api.Configuration;
using DataHub.Api.Domain;
using DataHub.Api.Serialization;
using DataHub.Api.Store;
using DataHub.Api.News;
using DataHub.Api.Csv;

namespace DataHub.Api.Persistence;

/// <summary>
/// Schema 2 adds news; schema 3 adds spot metadata and index composition. Earlier schemas remain readable.
/// Checksums detect corruption, not malicious tampering.
/// </summary>
public static class CheckpointSerializer
{
    public const int SchemaVersion = 3;

    private static readonly JsonSerializerOptions Json = DataHubJson.CreateOptions();

    public static byte[] Serialize(CheckpointPayload payload)
    {
        var sources = payload.Sources
            .OrderBy(source => source.SourceId, StringComparer.Ordinal)
            .Select(ToFile)
            .ToList();

        var sourcesBytes = JsonSerializer.SerializeToUtf8Bytes(sources, Json);
        var hash = Convert.ToHexString(SHA256.HashData(sourcesBytes)).ToLowerInvariant();
        var newsBytes = JsonSerializer.SerializeToUtf8Bytes(payload.News
            .OrderBy(item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(item => item.ArticleId, StringComparer.Ordinal).ToArray(), Json);
        var newsHash = Convert.ToHexString(SHA256.HashData(newsBytes)).ToLowerInvariant();
        var identitiesBytes = JsonSerializer.SerializeToUtf8Bytes(payload.NewsIdentities
            .OrderBy(item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(item => item.ArticleId, StringComparer.Ordinal).ToArray(), Json);
        var identitiesHash = Convert.ToHexString(SHA256.HashData(identitiesBytes)).ToLowerInvariant();
        var membershipsBytes = JsonSerializer.SerializeToUtf8Bytes(payload.Memberships
            .OrderBy(item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(item => item.IndexId, StringComparer.Ordinal)
            .ThenBy(item => item.InstrumentId, StringComparer.Ordinal).ToArray(), Json);
        var membershipsHash = Convert.ToHexString(SHA256.HashData(membershipsBytes)).ToLowerInvariant();
        var compositionStatusBytes = JsonSerializer.SerializeToUtf8Bytes(
            payload.CompositionStatus ?? new CompositionImportStatus(null, null, null, null, 0, 0, null), Json);
        var compositionStatusHash = Convert.ToHexString(SHA256.HashData(compositionStatusBytes)).ToLowerInvariant();

        using var buffer = new MemoryStream(sourcesBytes.Length + 512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteNumber("checkpointVersion", payload.Version);
            writer.WriteString("savedAtUtc", UtcDateTimeOffsetJsonConverter.FormatUtc(payload.SavedAtUtc));
            writer.WriteNumber("recordCount", payload.RecordCount);
            writer.WriteString("payloadSha256", hash);
            writer.WritePropertyName("sources");
            writer.WriteRawValue(sourcesBytes, skipInputValidation: true);
            writer.WriteString("newsSha256", newsHash);
            writer.WritePropertyName("news");
            writer.WriteRawValue(newsBytes, skipInputValidation: true);
            writer.WriteString("newsIdentitiesSha256", identitiesHash);
            writer.WritePropertyName("newsIdentities");
            writer.WriteRawValue(identitiesBytes, skipInputValidation: true);
            writer.WriteNumber("membershipCount", payload.Memberships.Count);
            writer.WriteString("membershipsSha256", membershipsHash);
            writer.WritePropertyName("memberships");
            writer.WriteRawValue(membershipsBytes, skipInputValidation: true);
            writer.WriteString("compositionStatusSha256", compositionStatusHash);
            writer.WritePropertyName("compositionStatus");
            writer.WriteRawValue(compositionStatusBytes, skipInputValidation: true);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>解析並驗證 checkpoint。任何問題都拋 <see cref="CheckpointFormatException"/>。</summary>
    public static CheckpointPayload Deserialize(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return DeserializeCore(bytes);
        }
        catch (CheckpointFormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new CheckpointFormatException($"Checkpoint content is invalid: {ex.Message}", ex);
        }
    }

    private static CheckpointPayload DeserializeCore(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new CheckpointFormatException("Checkpoint file is empty.");
        }

        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new CheckpointFormatException("Checkpoint root must be a JSON object.");
        }

        var schemaVersion = root.GetProperty("schemaVersion").GetInt32();
        if (schemaVersion > SchemaVersion)
        {
            throw new FutureCheckpointFormatException(schemaVersion);
        }
        if (schemaVersion is not (1 or 2 or SchemaVersion))
        {
            throw new CheckpointFormatException($"Unsupported checkpoint schemaVersion {schemaVersion} (expected 1, 2 or {SchemaVersion}).");
        }

        var sourcesElement = root.GetProperty("sources");
        var expectedHash = root.GetProperty("payloadSha256").GetString();
        var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcesElement.GetRawText()))).ToLowerInvariant();
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new CheckpointFormatException("Checkpoint checksum mismatch (file truncated or corrupted).");
        }

        var version = root.GetProperty("checkpointVersion").GetInt64();
        var savedAtUtc = root.GetProperty("savedAtUtc").Deserialize<DateTimeOffset>(Json);
        var declaredCount = root.GetProperty("recordCount").GetInt64();

        var files = sourcesElement.Deserialize<List<SourceFile>>(Json)
            ?? throw new CheckpointFormatException("Checkpoint 'sources' is null.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<SourceData>(files.Count);
        foreach (var file in files)
        {
            if (file is null)
                throw new CheckpointFormatException("Checkpoint contains a null source.");
            if (string.IsNullOrWhiteSpace(file.SourceId) || !seen.Add(file.SourceId))
            {
                throw new CheckpointFormatException($"Checkpoint contains a missing or duplicate sourceId '{file.SourceId}'.");
            }

            var records = file.Records ?? throw new CheckpointFormatException($"Source '{file.SourceId}' has null records.");
            var priceRecords = new List<PriceRecord>(records.Count);
            foreach (var record in records)
            {
                if (record is null || string.IsNullOrWhiteSpace(record.InstrumentId))
                {
                    throw new CheckpointFormatException($"Source '{file.SourceId}' contains a record without instrumentId.");
                }

                priceRecords.Add(new PriceRecord(
                    file.SourceId,
                    record.InstrumentId,
                    record.Price,
                    record.Currency ?? string.Empty,
                    record.SourceUpdatedAtUtc,
                    record.ReceivedAtUtc,
                    record.SourceRevision,
                    schemaVersion >= 3 ? record.ProviderSymbol : null,
                    schemaVersion >= 3 ? record.PriceKind : null));
            }

            var data = SourceData.FromRecords(
                file.SourceId,
                priceRecords,
                file.SnapshotRevision,
                file.LastSnapshotAtUtc,
                file.LastUpdateReceivedAtUtc);

            if (data.RecordCount != records.Count)
            {
                throw new CheckpointFormatException($"Source '{file.SourceId}' contains duplicate instrumentId values.");
            }

            sources.Add(data);
        }

        IReadOnlyList<NewsRecord> news = [];
        IReadOnlyList<NewsIdentity> identities = [];
        IReadOnlyList<IndexMembership> memberships = [];
        CompositionImportStatus? compositionStatus = null;
        if (schemaVersion >= 2)
        {
            var newsElement = root.GetProperty("news");
            var newsHash = root.GetProperty("newsSha256").GetString();
            var actualNewsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(newsElement.GetRawText()))).ToLowerInvariant();
            if (!string.Equals(newsHash, actualNewsHash, StringComparison.OrdinalIgnoreCase))
                throw new CheckpointFormatException("Checkpoint news checksum mismatch.");
            var items = newsElement.Deserialize<List<NewsRecord>>(Json)
                ?? throw new CheckpointFormatException("Checkpoint news is null.");
            var keys = new HashSet<(string, string)>();
            foreach (var item in items)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.SourceId) || string.IsNullOrWhiteSpace(item.ArticleId)
                    || string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Publisher)
                    || string.IsNullOrWhiteSpace(item.Language) || item.MappingWarnings is null
                    || !Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                    || !keys.Add((item.SourceId, item.ArticleId)))
                    throw new CheckpointFormatException("Checkpoint contains invalid or duplicate news.");
            }
            news = items;
            var identityElement = root.GetProperty("newsIdentities");
            var identityHash = root.GetProperty("newsIdentitiesSha256").GetString();
            var actualIdentityHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityElement.GetRawText()))).ToLowerInvariant();
            if (!string.Equals(identityHash, actualIdentityHash, StringComparison.OrdinalIgnoreCase))
                throw new CheckpointFormatException("Checkpoint news identity checksum mismatch.");
            var savedIdentities = identityElement.Deserialize<List<NewsIdentity>>(Json)
                ?? throw new CheckpointFormatException("Checkpoint news identities are null.");
            var seenIdentities = new HashSet<(string, string)>();
            foreach (var identity in savedIdentities)
                if (identity is null || string.IsNullOrWhiteSpace(identity.SourceId) || string.IsNullOrWhiteSpace(identity.ArticleId)
                    || !seenIdentities.Add((identity.SourceId, identity.ArticleId)))
                    throw new CheckpointFormatException("Checkpoint contains invalid or duplicate news identities.");
            identities = savedIdentities;
        }
        if (schemaVersion >= 3)
        {
            var membershipElement = root.GetProperty("memberships");
            var membershipHash = root.GetProperty("membershipsSha256").GetString();
            var actualMembershipHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(membershipElement.GetRawText()))).ToLowerInvariant();
            if (!string.Equals(membershipHash, actualMembershipHash, StringComparison.OrdinalIgnoreCase))
                throw new CheckpointFormatException("Checkpoint membership checksum mismatch.");
            var savedMemberships = membershipElement.Deserialize<List<IndexMembership>>(Json)
                ?? throw new CheckpointFormatException("Checkpoint memberships are null.");
            if (root.GetProperty("membershipCount").GetInt32() != savedMemberships.Count)
                throw new CheckpointFormatException("Checkpoint membershipCount mismatch.");
            var seenMemberships = new HashSet<(string, string, string)>();
            foreach (var membership in savedMemberships)
                if (membership is null
                    || string.IsNullOrWhiteSpace(membership.SourceId)
                    || string.IsNullOrWhiteSpace(membership.IndexId)
                    || !membership.IndexId.StartsWith("DEMO", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(membership.InstrumentId)
                    || membership.Weight is < 0 or > 1
                    || !seenMemberships.Add((membership.SourceId, membership.IndexId, membership.InstrumentId)))
                    throw new CheckpointFormatException("Checkpoint contains invalid or duplicate index memberships.");
            memberships = savedMemberships;

            var statusElement = root.GetProperty("compositionStatus");
            var statusHash = root.GetProperty("compositionStatusSha256").GetString();
            var actualStatusHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(statusElement.GetRawText()))).ToLowerInvariant();
            if (!string.Equals(statusHash, actualStatusHash, StringComparison.OrdinalIgnoreCase))
                throw new CheckpointFormatException("Checkpoint composition status checksum mismatch.");
            compositionStatus = statusElement.Deserialize<CompositionImportStatus>(Json)
                ?? throw new CheckpointFormatException("Checkpoint composition status is null.");
            if (compositionStatus.RowCount != memberships.Count || compositionStatus.ErrorCount < 0)
                throw new CheckpointFormatException("Checkpoint composition status is inconsistent.");
        }
        var payload = new CheckpointPayload(version, savedAtUtc, sources)
        {
            News = news,
            NewsIdentities = identities,
            Memberships = memberships,
            CompositionStatus = compositionStatus,
        };
        if (payload.RecordCount != declaredCount)
        {
            throw new CheckpointFormatException($"Checkpoint recordCount mismatch (declared {declaredCount}, actual {payload.RecordCount}).");
        }

        return payload;
    }

    private static SourceFile ToFile(SourceData source) => new()
    {
        SourceId = source.SourceId,
        SnapshotRevision = source.SnapshotRevision,
        LastSnapshotAtUtc = source.LastSnapshotAtUtc,
        LastUpdateReceivedAtUtc = source.LastUpdateReceivedAtUtc,
        Records = source.Records.Values
            .Select(record => new RecordFile
            {
                InstrumentId = record.InstrumentId,
                Price = record.Price,
                Currency = record.Currency,
                SourceUpdatedAtUtc = record.SourceUpdatedAtUtc,
                ReceivedAtUtc = record.ReceivedAtUtc,
                SourceRevision = record.SourceRevision,
                ProviderSymbol = record.ProviderSymbol,
                PriceKind = record.PriceKind,
            })
            .ToList(),
    };

    private sealed class SourceFile
    {
        public string SourceId { get; set; } = string.Empty;
        public long SnapshotRevision { get; set; }
        public DateTimeOffset? LastSnapshotAtUtc { get; set; }
        public DateTimeOffset? LastUpdateReceivedAtUtc { get; set; }
        public List<RecordFile>? Records { get; set; }
    }

    private sealed class RecordFile
    {
        public string InstrumentId { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public string? Currency { get; set; }
        public DateTimeOffset SourceUpdatedAtUtc { get; set; }
        public DateTimeOffset ReceivedAtUtc { get; set; }
        public long SourceRevision { get; set; }
        public string? ProviderSymbol { get; set; }
        public PriceKind? PriceKind { get; set; }
    }
}
