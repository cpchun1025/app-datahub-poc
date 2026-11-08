using System.Collections.Immutable;
using DataHub.Api.Domain;

namespace DataHub.Api.Store;

/// <summary>
/// 每個來源一個 <see cref="SourceData"/>（不可變）。寫入者喺 per-source lock 內計算新狀態再原子替換 reference，
/// lock 內只有記憶體運算；reader 永遠唔需要 lock，攞到嘅係一個穩定嘅切面。
/// 一個來源嘅寫入唔會阻塞其他來源。
/// </summary>
public sealed class LatestStateStore : ILatestStateStore
{
    private readonly Dictionary<string, SourceSlot> _slotsById;
    private readonly SourceSlot[] _slots;
    private long _version;

    public LatestStateStore(IEnumerable<string> sourceIds)
    {
        var ids = sourceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        _slots = ids.Select(id => new SourceSlot(id)).ToArray();
        _slotsById = _slots.ToDictionary(slot => slot.SourceId, StringComparer.OrdinalIgnoreCase);
        SourceIds = ids;
    }

    public long Version => Volatile.Read(ref _version);

    public IReadOnlyList<string> SourceIds { get; }

    public UpdateApplyResult ApplyUpdates(string sourceId, IReadOnlyList<PriceUpdate> updates, DateTimeOffset receivedAtUtc)
    {
        var slot = GetSlot(sourceId);
        if (updates.Count == 0)
        {
            return default;
        }

        lock (slot.WriteLock)
        {
            var current = slot.Current;
            var builder = current.Records.ToBuilder();
            var applied = 0;

            foreach (var update in updates)
            {
                if (ShouldApply(builder, current.SnapshotRevision, update))
                {
                    builder[update.InstrumentId] = ToRecord(slot.SourceId, update, receivedAtUtc);
                    applied++;
                }
            }

            if (applied > 0)
            {
                slot.Current = current with { Records = builder.ToImmutable(), LastUpdateReceivedAtUtc = receivedAtUtc };
                Interlocked.Increment(ref _version);
            }

            return new UpdateApplyResult(applied, updates.Count - applied);
        }
    }

    public SnapshotApplyResult ApplySnapshot(
        string sourceId,
        SourceSnapshot snapshot,
        DateTimeOffset receivedAtUtc,
        SnapshotApplyMode mode)
    {
        var slot = GetSlot(sourceId);
        var incoming = Deduplicate(snapshot.Records);

        lock (slot.WriteLock)
        {
            var current = slot.Current;

            if (mode == SnapshotApplyMode.Merge && snapshot.Revision < current.SnapshotRevision)
            {
                // 比已套用 snapshot 更舊，套用會令資料倒退。
                return new SnapshotApplyResult(0, 0, 0, 0, current.RecordCount, Skipped: true);
            }

            var outcome = mode == SnapshotApplyMode.Replace
                ? BuildReplacement(slot.SourceId, current, incoming, receivedAtUtc)
                : BuildMerge(slot.SourceId, current, incoming, snapshot.Revision, receivedAtUtc);

            slot.Current = current with
            {
                Records = outcome.Records,
                SnapshotRevision = snapshot.Revision,
                LastSnapshotAtUtc = receivedAtUtc,
            };
            Interlocked.Increment(ref _version);

            return new SnapshotApplyResult(
                outcome.Added, outcome.Updated, outcome.Removed, outcome.KeptNewer, outcome.Records.Count, Skipped: false);
        }
    }

    public void Restore(SourceData data)
    {
        var slot = GetSlot(data.SourceId);
        lock (slot.WriteLock)
        {
            slot.Current = data with { SourceId = slot.SourceId };
            Interlocked.Increment(ref _version);
        }
    }

    public SourceData? GetSource(string sourceId)
        => _slotsById.TryGetValue(sourceId, out var slot) ? slot.Current : null;

    public IReadOnlyList<SourceData> GetAllSources()
    {
        var result = new SourceData[_slots.Length];
        for (var i = 0; i < _slots.Length; i++)
        {
            result[i] = _slots[i].Current;
        }

        return result;
    }

    public PriceRecord? Find(string sourceId, string instrumentId)
    {
        var source = GetSource(sourceId);
        return source is not null && source.Records.TryGetValue(instrumentId, out var record) ? record : null;
    }

    public PriceQueryResult Query(PriceQuery query)
        => PriceQueryEngine.Execute(GetAllSources(), query);

    public StoreCapture Capture()
    {
        // version 必須喺讀取各來源切面之前讀取：寧願之後多保存一次，都唔可以記錄一個未包含喺切面入面嘅 version。
        var version = Volatile.Read(ref _version);
        return new StoreCapture(version, GetAllSources());
    }

    private SourceSlot GetSlot(string sourceId)
        => _slotsById.TryGetValue(sourceId, out var slot)
            ? slot
            : throw new ArgumentException($"未註冊嘅 sourceId '{sourceId}'。", nameof(sourceId));

    private static bool ShouldApply(
        ImmutableSortedDictionary<string, PriceRecord>.Builder records,
        long snapshotRevision,
        PriceUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.InstrumentId))
        {
            return false;
        }

        // 已有 record：只有 revision 較新先取代（重複或遲到嘅舊值被忽略）。
        // 未有 record：只有 snapshot cut 之後先出現嘅 key 先有效；cut 或之前嘅遲到 update 唔可以令已被 snapshot 移除嘅 key 復活。
        return records.TryGetValue(update.InstrumentId, out var existing)
            ? update.Revision > existing.SourceRevision
            : update.Revision > snapshotRevision;
    }

    private static Dictionary<string, PriceUpdate> Deduplicate(IReadOnlyList<PriceUpdate> records)
    {
        var map = new Dictionary<string, PriceUpdate>(records.Count, StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.InstrumentId))
            {
                continue;
            }

            if (!map.TryGetValue(record.InstrumentId, out var existing) || record.Revision > existing.Revision)
            {
                map[record.InstrumentId] = record;
            }
        }

        return map;
    }

    private static MergeOutcome BuildReplacement(
        string sourceId,
        SourceData current,
        Dictionary<string, PriceUpdate> incoming,
        DateTimeOffset receivedAtUtc)
    {
        var builder = SourceData.EmptyRecords.ToBuilder();
        int added = 0, updated = 0;

        foreach (var update in incoming.Values)
        {
            builder[update.InstrumentId] = ToRecord(sourceId, update, receivedAtUtc);
            if (current.Records.ContainsKey(update.InstrumentId))
            {
                updated++;
            }
            else
            {
                added++;
            }
        }

        return new MergeOutcome(builder.ToImmutable(), added, updated, current.RecordCount - updated, 0);
    }

    private static MergeOutcome BuildMerge(
        string sourceId,
        SourceData current,
        Dictionary<string, PriceUpdate> incoming,
        long snapshotRevision,
        DateTimeOffset receivedAtUtc)
    {
        var builder = current.Records.ToBuilder();
        int added = 0, updated = 0, keptNewer = 0, removed = 0;

        foreach (var update in incoming.Values)
        {
            if (builder.TryGetValue(update.InstrumentId, out var existing))
            {
                if (update.Revision > existing.SourceRevision)
                {
                    builder[update.InstrumentId] = ToRecord(sourceId, update, receivedAtUtc);
                    updated++;
                }
                else if (update.Revision < existing.SourceRevision)
                {
                    // snapshot 喺等待期間，較新嘅 update 已經先到；保留較新值。
                    keptNewer++;
                }
            }
            else
            {
                builder[update.InstrumentId] = ToRecord(sourceId, update, receivedAtUtc);
                added++;
            }
        }

        // Membership：唔喺 snapshot、而且 revision 喺 cut 或之前嘅 record 已經唔係成員，移除。
        // revision 大過 cut 嘅 record 係 snapshot 之後先新增／更新，屬有效資料，保留。
        foreach (var existing in current.Records.Values)
        {
            if (!incoming.ContainsKey(existing.InstrumentId) && existing.SourceRevision <= snapshotRevision)
            {
                builder.Remove(existing.InstrumentId);
                removed++;
            }
        }

        return new MergeOutcome(builder.ToImmutable(), added, updated, removed, keptNewer);
    }

    private static PriceRecord ToRecord(string sourceId, PriceUpdate update, DateTimeOffset receivedAtUtc)
        => new(
            sourceId,
            update.InstrumentId,
            update.Price,
            update.Currency,
            update.SourceUpdatedAtUtc.ToUniversalTime(),
            receivedAtUtc.ToUniversalTime(),
            update.Revision);

    private readonly record struct MergeOutcome(
        ImmutableSortedDictionary<string, PriceRecord> Records,
        int Added,
        int Updated,
        int Removed,
        int KeptNewer);

    private sealed class SourceSlot(string sourceId)
    {
        private SourceData _current = SourceData.Empty(sourceId);

        public string SourceId { get; } = sourceId;

        public object WriteLock { get; } = new();

        public SourceData Current
        {
            get => Volatile.Read(ref _current);
            set => Volatile.Write(ref _current, value);
        }
    }
}
