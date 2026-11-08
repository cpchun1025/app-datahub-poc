using Microsoft.Extensions.Time.Testing;

namespace DataHub.Api.Tests.Support;

/// <summary>
/// FakeTimeProvider 加上「記低 runner 要求過嘅 timer」。退避時間係 runner 自己決定嘅 duration，同測試幾時推進假時間無關，
/// 所以可以決定性咁驗證 1s、2s、4s…cap 嘅序列，唔使靠真實 sleep 或者撞 timer 註冊嘅時序。
/// </summary>
internal sealed class RecordingTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly List<TimeSpan> _oneShotDelays = [];

    /// <summary>所有一次性 timer（Task.Delay）要求嘅等待時間，按建立次序；PeriodicTimer 唔計。</summary>
    public IReadOnlyList<TimeSpan> OneShotDelays
    {
        get
        {
            lock (_oneShotDelays)
            {
                return [.. _oneShotDelays];
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period == Timeout.InfiniteTimeSpan)
        {
            lock (_oneShotDelays)
            {
                _oneShotDelays.Add(dueTime);
            }
        }

        return base.CreateTimer(callback, state, dueTime, period);
    }
}
