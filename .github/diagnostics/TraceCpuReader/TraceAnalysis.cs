using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Metadata;
using Microsoft.Windows.EventTracing.Processes;

namespace TraceCpuReader;

internal static class TraceAnalysis
{
    private static void ReadCore(string path, uint processId, uint[] threadIds)
    {
        string toolkit = Path.Combine(AppContext.BaseDirectory, "wpt");
        if (!File.Exists(Path.Combine(toolkit, "perfcore.dll"))
            || !File.Exists(Path.Combine(toolkit, "perf_nt.dll"))
            || !Directory.Exists(Path.Combine(toolkit, "Catalog")))
            throw new ReaderFailure(ReaderStatus.UnsupportedOrMissingData);
        var settings = new TraceProcessorSettings
        {
            AllowLostEvents = false,
            AllowTimeInversion = false,
            SuppressFirstTimeSetupMessage = true,
            // Use the NuGet-local toolkit without selecting or modifying a system installation.
            ToolkitPath = toolkit,
            CatalogPath = Path.Combine(toolkit, "Catalog")
        };
        using ITraceProcessor trace = TraceProcessor.Create(path, settings);
        ITraceMetadata metadata = trace.UseMetadata();
        IPendingResult<IProcessDataSource> processes = trace.UseProcesses();
        IPendingResult<IThreadDataSource> threads = trace.UseThreads();
        IPendingResult<ICpuSchedulingDataSource> scheduling = trace.UseCpuSchedulingData();
        trace.Process();
        ReportMetadata(metadata);
        if (!processes.HasResult || !threads.HasResult || !scheduling.HasResult)
            throw new ReaderFailure(ReaderStatus.UnsupportedOrMissingData);
        Target[] targets = ResolveTargets(processes.Result, threads.Result, processId, threadIds);
        Analyze(scheduling.Result, targets);
        foreach (Target target in targets)
            target.Report(processId);
        foreach (Target target in targets)
        {
            if (target.Wait.CompleteCount + target.Ready.CompleteCount == 0)
                throw new ReaderFailure(ReaderStatus.NoTargetObservations);
        }
    }

    internal static void Read(string path, uint processId, uint[] threadIds)
    {
        try
        {
            ReadCore(path, processId, threadIds);
        }
        catch (Exception error)
        {
            for (Exception? current = error; current is not null; current = current.InnerException)
            {
                if (current is TraceLostEventsException)
                    throw new ReaderFailure(ReaderStatus.LostData, current.HResult);
                if (current is TraceTimeInversionException or InvalidTraceDataException or InvalidTraceFormatException)
                    throw new ReaderFailure(ReaderStatus.ParseFailure, current.HResult);
            }
            throw;
        }
    }

    private static void ReportMetadata(ITraceMetadata metadata)
    {
        Console.WriteLine("NATIVE_TYPED_CPU sdkMajor=1 sdkMinor=12 sdkPatch=10 toolkitMajor=1 toolkitMinor=11 toolkitPatch=0 allowLostEvents=0 allowTimeInversion=0");
        Console.WriteLine($"NATIVE_TYPED_CPU lostEvents={metadata.LostEventCount} lostBuffers={metadata.LostBufferCount} traceHeaderKernelEventVersion={metadata.KernelEventVersion}");
        if (metadata.LostEventCount < 0 || metadata.LostBufferCount < 0)
            throw new ReaderFailure(ReaderStatus.ParseFailure);
        if (metadata.LostEventCount != 0 || metadata.LostBufferCount != 0)
            throw new ReaderFailure(ReaderStatus.LostData);
        decimal headerSeconds = (decimal)(metadata.StopTime - metadata.StartTime).Ticks / TimeSpan.TicksPerSecond;
        if (headerSeconds < 0)
            throw new ReaderFailure(ReaderStatus.ParseFailure);
        Console.WriteLine($"NATIVE_TYPED_CPU traceHeaderDurationSeconds={headerSeconds}");
        TraceDuration displayed = metadata.AnalyzerDisplayedDuration;
        if (displayed.HasValue && !displayed.IsPartial)
        {
            if (displayed.TotalSeconds < 0)
                throw new ReaderFailure(ReaderStatus.ParseFailure);
            Console.WriteLine($"NATIVE_TYPED_CPU analyzerDisplayedDurationKnown=1 analyzerDisplayedDurationSeconds={displayed.TotalSeconds}");
        }
        else
            Console.WriteLine("NATIVE_TYPED_CPU analyzerDisplayedDurationKnown=0");
        // Trace header and WPA display durations do not establish coverage of the consumer window.
        Console.WriteLine("NATIVE_TYPED_CPU eventSchemaUnknown=1 eventVersionUnknown=1 nativeModeUnknown=1 stackAvailabilityUnknown=1 rootCauseUnknown=1 consumerWindowCompletenessUnknown=1 intervalEnumerationCompletenessUnknown=1 unobservedLeadingIntervalsUnknown=1 unobservedTrailingIntervalsUnknown=1");
    }

    private static Target[] ResolveTargets(IProcessDataSource processes, IThreadDataSource threads,
        uint processId, uint[] threadIds)
    {
        IProcess? process = null;
        foreach (IProcess candidate in processes.Processes)
        {
            if (unchecked((uint)candidate.Id) != processId)
                continue;
            if (process is not null)
                throw new ReaderFailure(ReaderStatus.AmbiguousIdentity);
            process = candidate;
        }
        if (process is null)
            throw new ReaderFailure(ReaderStatus.NoTargetObservations);
        var targets = new Target[threadIds.Length];
        for (int i = 0; i < threadIds.Length; i++)
        {
            IThread? thread = null;
            foreach (IThread candidate in threads.Threads)
            {
                if (unchecked((uint)candidate.Id) != threadIds[i])
                    continue;
                // Reject reused TIDs even when the other instance belongs to a different process.
                if (thread is not null)
                    throw new ReaderFailure(ReaderStatus.AmbiguousIdentity);
                thread = candidate;
            }
            if (thread is null)
                throw new ReaderFailure(ReaderStatus.NoTargetObservations);
            if (unchecked((uint)thread.ProcessId) != processId || !SameProcess(thread.Process, process))
                throw new ReaderFailure(ReaderStatus.AmbiguousIdentity);
            targets[i] = new Target(process, thread);
        }
        return targets;
    }

    private static bool HasFullTimestamp(TraceTimestamp? time) =>
        time.HasValue && time.Value.HasValue && !time.Value.IsPartial;

    private static bool SameProcess(IProcess? actual, IProcess expected) =>
        actual is not null && actual.Id == expected.Id
        && (ReferenceEquals(actual, expected)
            || (HasFullTimestamp(actual.CreateTime) && HasFullTimestamp(expected.CreateTime)
                && Nullable.Equals(actual.CreateTime, expected.CreateTime)
                && Nullable.Equals(actual.ExitTime, expected.ExitTime)));

    private static void Analyze(ICpuSchedulingDataSource data, Target[] targets)
    {
        Console.WriteLine($"NATIVE_TYPED_CPU contextSwitchRecords={data.ContextSwitches.Count} readyThreadRecords={data.ReadyThreadEvents.Count} cpuTimeSliceRecords={data.CpuTimeSlices.Count} threadActivityRecords={data.ThreadActivity.Count}");
        if (data.ContextSwitches.Count == 0 || data.ReadyThreadEvents.Count == 0)
            throw new ReaderFailure(ReaderStatus.UnsupportedOrMissingData);
        var byId = new Dictionary<uint, Target>(targets.Length);
        foreach (Target target in targets)
            byId.Add(unchecked((uint)target.Thread.Id), target);
        foreach (ICpuThreadActivity activity in data.ThreadActivity)
        {
            int? id = activity.Thread?.Id ?? activity.SwitchIn?.ThreadId;
            if (!id.HasValue || !byId.TryGetValue(unchecked((uint)id.Value), out Target? target))
                continue;
            target.ValidateIdentity(activity.Thread, activity.Process);
            target.ActivityCount++;
            ICpuThreadActivity2 extended = activity.AsCpuThreadActivity2();
            ICpuWait? wait = extended.Wait;
            if (wait is null)
                target.WaitMissing++;
            else
            {
                target.ValidateIdentity(wait.Thread, wait.Process);
                if (wait.ThreadId != target.Thread.Id)
                    throw new ReaderFailure(ReaderStatus.AmbiguousIdentity);
                target.AddWait(wait);
            }
            ICpuReadyInterval? ready = extended.ReadyInterval;
            if (ready is null)
                target.ReadyMissing++;
            else
            {
                target.ValidateIdentity(ready.Thread, ready.Process);
                if (ready.ThreadId != target.Thread.Id)
                    throw new ReaderFailure(ReaderStatus.AmbiguousIdentity);
                target.Ready.Add(ready.StartTime, ready.StopTime, ready.Duration);
            }
        }
    }

    internal sealed class Target(IProcess process, IThread thread)
    {
        private static readonly KernelWaitReason[] Reasons = Enum.GetValues<KernelWaitReason>();
        private readonly long[] reasonCounts = new long[Reasons.Length];
        private readonly decimal[] reasonSeconds = new decimal[Reasons.Length];
        private long reasonUnavailable;
        internal IThread Thread { get; } = thread;
        internal IntervalTotals Wait { get; } = new();
        internal IntervalTotals Ready { get; } = new();
        internal long ActivityCount { get; set; }
        internal long WaitMissing { get; set; }
        internal long ReadyMissing { get; set; }

        internal void ValidateIdentity(IThread? actualThread, IProcess? actualProcess)
        {
            if (actualThread is null || actualThread.Id != Thread.Id
                || actualThread.ProcessId != Thread.ProcessId
                || (!ReferenceEquals(actualThread, Thread)
                    && (!HasFullTimestamp(actualThread.CreateTime) || !HasFullTimestamp(Thread.CreateTime)
                        || !Nullable.Equals(actualThread.CreateTime, Thread.CreateTime)
                        || !Nullable.Equals(actualThread.ExitTime, Thread.ExitTime)))
                || !SameProcess(actualThread.Process, process) || !SameProcess(actualProcess, process))
                throw new ReaderFailure(ReaderStatus.AmbiguousIdentity);
        }

        internal void AddWait(ICpuWait wait)
        {
            if (!Wait.Add(wait.StartTime, wait.StopTime, wait.Duration))
                return;
            // The exact SDK exposes a nullable KernelWaitReason, not a free-form string.
            KernelWaitReason? reason = wait.WaitReason;
            int index = reason.HasValue ? Array.IndexOf(Reasons, reason.Value) : -1;
            if (index < 0)
            {
                reasonUnavailable++;
                return;
            }
            reasonCounts[index]++;
            reasonSeconds[index] += wait.Duration!.Value.TotalSeconds;
        }

        internal void Report(uint processId)
        {
            uint tid = unchecked((uint)Thread.Id);
            int boundaryUnknown = !HasFullTimestamp(process.CreateTime) || !HasFullTimestamp(process.ExitTime)
                || !HasFullTimestamp(Thread.CreateTime) || !HasFullTimestamp(Thread.ExitTime) ? 1 : 0;
            Console.WriteLine($"NATIVE_TYPED_CPU pid={processId} tid={tid} identityInstances=1 identitySourceTypedRow=1 identityBoundaryUnknown={boundaryUnknown} activityCount={ActivityCount} waitMissing={WaitMissing} readyMissing={ReadyMissing}");
            Console.WriteLine($"NATIVE_TYPED_CPU pid={processId} tid={tid} waitIntervals={Wait.SeenCount} waitComplete={Wait.CompleteCount} waitIncomplete={Wait.IncompleteCount} waitDuplicateLinks={Wait.DuplicateCount} waitSeconds={Wait.Seconds} readyIntervals={Ready.SeenCount} readyComplete={Ready.CompleteCount} readyIncomplete={Ready.IncompleteCount} readyDuplicateLinks={Ready.DuplicateCount} readySeconds={Ready.Seconds} reasonUnavailable={reasonUnavailable}");
            for (int i = 0; i < Reasons.Length; i++)
            {
                if (reasonCounts[i] != 0)
                    Console.WriteLine($"NATIVE_TYPED_CPU pid={processId} tid={tid} waitReason={Reasons[i]} waitComplete={reasonCounts[i]} waitSeconds={reasonSeconds[i]}");
            }
        }
    }

    internal sealed class IntervalTotals
    {
        // SDK interval links can refer to the same interval from more than one activity.
        private readonly HashSet<(TraceTimestamp? Start, TraceTimestamp Stop)> seen = [];
        internal long SeenCount => seen.Count;
        internal long CompleteCount { get; private set; }
        internal long IncompleteCount { get; private set; }
        internal long DuplicateCount { get; private set; }
        internal decimal Seconds { get; private set; }

        internal bool Add(TraceTimestamp? start, TraceTimestamp stop, TraceDuration? duration)
        {
            if (!seen.Add((start, stop)))
            {
                DuplicateCount++;
                return false;
            }
            if (!start.HasValue || !start.Value.HasValue || start.Value.IsPartial
                || !stop.HasValue || stop.IsPartial || !duration.HasValue
                || !duration.Value.HasValue || duration.Value.IsPartial)
            {
                IncompleteCount++;
                return false;
            }
            if (stop < start.Value || duration.Value.TotalSeconds < 0)
                throw new ReaderFailure(ReaderStatus.ParseFailure);
            CompleteCount++;
            Seconds += duration.Value.TotalSeconds;
            return true;
        }
    }
}