using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

// Temporary, explicitly scoped diagnostics only.
// _Total covers the machine, not this process, root or native operation.
// Interval averages and instantaneous queue samples cannot establish causality or describe the unsampled tail after the observer's last scheduled snapshot.
internal sealed class WindowsPhysicalDiskCounters : IDisposable
{
    // PDH_FMT_DOUBLE | PDH_FMT_NOSCALE: retain seconds/counts/rates, not display scaling.
    private const uint FormatDoubleNoScale = 0x00000200 | 0x00001000;
    private static readonly string[] CounterPaths =
    [
        @"\PhysicalDisk(_Total)\Avg. Disk sec/Transfer",
        @"\PhysicalDisk(_Total)\Current Disk Queue Length",
        @"\PhysicalDisk(_Total)\Disk Transfers/sec"
    ];

    // Numeric counter IDs: 1 = seconds/transfer; 2 = outstanding requests, including requests in service, at the sampling instant; 3 = read + write transfers/second over the collection interval.
    // Numeric stages: 0 = unsupported OS, 1 = open, 2 = add, 3 = collect, 4 = format, 5 = close.
    // api=1 means the call returned (not necessarily success); api=0 means not attempted;
    // api=-1 means a managed/interop exception. status=-1 and cstatus=4294967295 mean unknown.
    private readonly long _observation;
    private readonly nint[] _counters = new nint[CounterPaths.Length];
    private QueryHandle? _query;
    private long _previousCollected;
    private bool _ready;

    public WindowsPhysicalDiskCounters(long observation)
    {
        _observation = observation;
        if (!OperatingSystem.IsWindows())
        {
            Emit(0, 0, 0, 0, -1);
            return;
        }

        var stage = 1;
        var counter = 0;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var status = PdhOpenQueryW(null, 0, out var query);
            _query = query;
            Emit(0, stage, counter, 1, status, started: started);
            if (status != 0 || query.IsInvalid)
                return;

            stage = 2;
            for (var i = 0; i < CounterPaths.Length; i++)
            {
                counter = i + 1;
                started = Stopwatch.GetTimestamp();
                status = PdhAddEnglishCounterW(query, CounterPaths[i], 0, out _counters[i]);
                Emit(0, stage, counter, 1, status, started: started);
                if (status != 0 || _counters[i] == 0)
                    return;
            }

            stage = 3;
            counter = 0;
            started = Stopwatch.GetTimestamp();
            status = PdhCollectQueryData(query);
            _previousCollected = Stopwatch.GetTimestamp();
            Emit(0, stage, counter, 1, status, started: started, collected: _previousCollected);
            // Rate/average counters need two successful collections: baseline now, then the existing 15/30/45-second callbacks.
            _ready = status == 0;
        }
        catch (Exception error)
        {
            // This optional diagnostic boundary must not replace the consumer's failure.
            Emit(0, stage, counter, -1, -1, error.HResult, started: started);
        }
        finally
        {
            if (!_ready)
                Dispose();
        }
    }

    // Observation serializes snapshots and drains its timer before calling Dispose.
    public void Emit(int snapshot)
    {
        if (!_ready || _query is not { } query)
            return;

        var stage = 3;
        var counter = 0;
        var started = Stopwatch.GetTimestamp();
        var healthy = false;
        try
        {
            var status = PdhCollectQueryData(query);
            var collected = Stopwatch.GetTimestamp();
            var interval = collected - _previousCollected;
            _previousCollected = collected;
            Emit(snapshot, stage, counter, 1, status, started: started,
                collected: collected, interval: interval);
            if (status != 0)
                return;

            healthy = true;
            stage = 4;
            for (var i = 0; i < _counters.Length; i++)
            {
                counter = i + 1;
                started = Stopwatch.GetTimestamp();
                // The unknown CStatus sentinel is retained unless the API writes a status.
                // Both API status and CStatus must succeed before publishing a value.
                var value = new FormattedCounterValue { Status = uint.MaxValue };
                status = PdhGetFormattedCounterValue(_counters[i], FormatDoubleNoScale, 0, ref value);
                var valid = status == 0 && value.Status is 0 or 1
                    && double.IsFinite(value.Value) && value.Value >= 0;
                Emit(snapshot, stage, counter, 1, status, cstatus: value.Status,
                    started: started, collected: collected, interval: interval,
                    value: valid ? value.Value : null);
                healthy &= valid;
            }
        }
        catch (Exception error)
        {
            healthy = false;
            Emit(snapshot, stage, counter, -1, -1, error.HResult, started: started);
        }
        finally
        {
            // Counter handles are borrowed from the query, not independently owned handles.
            GC.KeepAlive(query);
            // Failed collection or formatting closes the query after the remaining fixed counters in this sample.
            if (!healthy)
                Dispose();
        }
    }

    public void Dispose()
    {
        _ready = false;
        var query = Interlocked.Exchange(ref _query, null);
        if (query is null)
            return;
        var started = Stopwatch.GetTimestamp();
        var hadHandle = !query.IsInvalid;
        query.Dispose();
        if (hadHandle)
            Emit(0, 5, 0, query.CloseError == 0 ? 1 : -1, query.CloseStatus,
                query.CloseError, started: started);
    }

    private void Emit(int snapshot, int stage, int counter, int api, long status,
        int error = 0, uint cstatus = uint.MaxValue, long started = 0,
        long collected = 0, long interval = 0, double? value = null)
    {
        try
        {
            var now = Stopwatch.GetTimestamp();
            // No provider strings, paths, handles or exception messages enter the trace.
            var numericValue = value is { } number
                ? $" value={number.ToString("R", CultureInfo.InvariantCulture)}" : string.Empty;
            Trace.WriteLine($"SANDBOX_MACHINE_DISK pid={Environment.ProcessId} observation={_observation} snapshot={snapshot} stage={stage} counter={counter} api={api} status={status} error={error} cstatus={cstatus} valid={(value.HasValue ? 1 : 0)} ticks={now} call-ticks={(started == 0 ? 0 : now - started)} collected-ticks={collected} interval-ticks={interval} frequency={Stopwatch.Frequency}{numericValue}");
        }
        catch (Exception) { }
    }

    // Native PDH_FMT_COUNTERVALUE places DWORD CStatus at offset 0 and its 8-byte-aligned union at offset 8.
    // The total size is 16 bytes under the Windows SDK default ABI.
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FormattedCounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    private sealed class QueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public QueryHandle() : base(true) { }
        public long CloseStatus { get; private set; } = -1;
        public int CloseError { get; private set; }

        protected override bool ReleaseHandle()
        {
            try
            {
                CloseStatus = PdhCloseQuery(handle);
                return CloseStatus == 0;
            }
            catch (Exception error)
            {
                // SafeHandle owns abandoned and partially constructed observations.
                // Release errors are retained numerically for explicit disposal; finalization remains silent.
                CloseError = error.HResult;
                return false;
            }
        }
    }

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? source, nuint userData, out QueryHandle query);

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(QueryHandle query, string path,
        nuint userData, out nint counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(QueryHandle query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(nint counter, uint format,
        nint type, ref FormattedCounterValue value);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(nint query);
}
