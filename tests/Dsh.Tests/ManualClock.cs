namespace Dsh.Tests;

/** Test clock: wall time and timestamps move only when the test advances it. */
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long GetTimestamp() => _utcNow.UtcTicks;

    public void Advance(TimeSpan duration) => _utcNow += duration;
}
