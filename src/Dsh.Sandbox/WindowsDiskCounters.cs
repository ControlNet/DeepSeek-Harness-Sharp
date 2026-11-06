using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dsh.Sandbox;

// Temporary machine aggregates cannot identify a device, filesystem filter or the cause of an individual wait.
internal sealed class WindowsDiskCounters : IDisposable
{
    private const int CounterCount = 4;
    internal const int MaximumNumericArrayBytes = CounterCount * (sizeof(long) + sizeof(uint));
    private const uint Success = 0;
    private const uint NewData = 1;
    private const uint Unavailable = uint.MaxValue;
    private const uint FormatDouble = 0x00000200;
    private const uint FormatNoScale = 0x00001000;
    private const uint FormatNoCap100 = 0x00008000;
    private readonly nint[] _counters = new nint[CounterCount];
    private readonly uint[] _addStatuses = [Unavailable, Unavailable, Unavailable, Unavailable];
    private readonly long _initializationStarted = Stopwatch.GetTimestamp();
    private readonly long _initializationFinished;
    private readonly uint _openStatus = Unavailable;
    private readonly uint _baselineStatus = Unavailable;
    private nint _query;
    private long _previousCollectionStarted;
    private int _exception;

    internal WindowsDiskCounters()
    {
        try
        {
            _openStatus = PdhOpenQueryW(null, 0, out _query);
            if (_openStatus == Success)
            {
                for (var index = 0; index < CounterCount; index++)
                    _addStatuses[index] = PdhAddEnglishCounterW(_query, CounterPath(index), 0, out _counters[index]);
                _previousCollectionStarted = Stopwatch.GetTimestamp();
                _baselineStatus = PdhCollectQueryData(_query);
            }
        }
        catch (Exception error)
        {
            _exception = error.HResult;
            Dispose();
        }
        _initializationFinished = Stopwatch.GetTimestamp();
    }

    internal void Dump(int sample)
    {
        var started = Stopwatch.GetTimestamp();
        var previous = _previousCollectionStarted;
        var status = Unavailable;
        try
        {
            if (_query != 0) status = PdhCollectQueryData(_query);
        }
        catch (Exception error) { _exception = error.HResult; }
        var collected = Stopwatch.GetTimestamp();
        _previousCollectionStarted = started;
        Trace.WriteLine($"IO_STAGE_DISK sample={sample} machineAggregate=1 consumerExclusive=0 physicalDiskTotal=1 openStatus={_openStatus} baselineStatus={_baselineStatus} collectStatus={status} exception={_exception} previousCollectionStarted={previous} collectionStarted={started} collectionFinished={collected} initializationStarted={_initializationStarted} initializationFinished={_initializationFinished} providerNativeAllocationUnknown=1");
        for (var index = 0; index < CounterCount; index++)
        {
            var value = ReadCounter(index, status == Success);
            Trace.WriteLine($"IO_STAGE_DISK_VALUE sample={sample} metric={Metric(index)} addStatus={_addStatuses[index]} formatStatus={value.FormatStatus} counterStatus={value.CounterStatus} counterType={value.CounterType} known={(value.Known ? 1 : 0)} value={value.Value} ticks={Stopwatch.GetTimestamp()} machineAggregate=1 consumerExclusive=0");
        }
        Trace.WriteLine($"IO_STAGE_DISK_COST sample={sample} started={started} finished={Stopwatch.GetTimestamp()} counterCountMaximum={CounterCount} queriesMaximum=1 numericArrayBytesMaximum={MaximumNumericArrayBytes} providerNativeAllocationUnknown=1 ratesUsePreviousAndCurrentProviderSamples=1 samplingAndLoggingPerturbationUnknown=1");
    }

    private CounterValue ReadCounter(int index, bool fresh)
    {
        var formatStatus = Unavailable;
        uint counterType = 0;
        var value = new FormattedCounterValue { Status = Unavailable, Value = double.NaN };
        try
        {
            if (fresh && _counters[index] != 0)
                formatStatus = PdhGetFormattedCounterValue(_counters[index], FormatDouble | FormatNoScale | FormatNoCap100,
                    out counterType, out value);
        }
        catch (Exception error) { _exception = error.HResult; }
        var scaled = value.Value * Scale(index);
        var known = fresh && _addStatuses[index] == Success && formatStatus == Success
            && value.Status is Success or NewData && double.IsFinite(scaled) && scaled >= 0 && scaled < long.MaxValue;
        return new CounterValue(formatStatus, value.Status, counterType, known, known ? (long)scaled : -1);
    }

    public void Dispose()
    {
        var query = Interlocked.Exchange(ref _query, 0);
        if (query == 0) return;
        var status = Unavailable;
        var started = Stopwatch.GetTimestamp();
        try { status = PdhCloseQuery(query); }
        catch (Exception error) { _exception = error.HResult; }
        var finished = Stopwatch.GetTimestamp();
        Trace.WriteLine($"IO_STAGE_DISK_CLOSE status={status} exception={_exception} startedTicks={started} finishedTicks={finished} counterCountMaximum={CounterCount} queryCountMaximum=1");
    }

    private static string CounterPath(int index) => index switch
    {
        0 => @"\PhysicalDisk(_Total)\Avg. Disk sec/Read",
        1 => @"\PhysicalDisk(_Total)\Disk Reads/sec",
        2 => @"\PhysicalDisk(_Total)\Current Disk Queue Length",
        3 => @"\PhysicalDisk(_Total)\Disk Read Bytes/sec",
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private static string Metric(int index) => index switch
    {
        0 => "readLatencyMicroseconds",
        1 => "readsPerSecondMilli",
        2 => "queueLengthMilli",
        3 => "readBytesPerSecond",
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private static long Scale(int index) => index switch
    {
        0 => 1_000_000,
        1 or 2 => 1_000,
        3 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private readonly record struct CounterValue(uint FormatStatus, uint CounterStatus, uint CounterType, bool Known, long Value);

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FormattedCounterValue
    {
        [FieldOffset(0)] internal uint Status;
        [FieldOffset(8)] internal double Value;
    }

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, nuint userData, out nint query);

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, out uint counterType,
        out FormattedCounterValue value);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(nint query);
}
