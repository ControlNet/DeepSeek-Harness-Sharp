using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Metadata;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Symbols;

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
        // Read captured stacks through the NuGet-local SDK.
        IPendingResult<IStackDataSource> stacks = trace.UseStacks();
        trace.Process();
        ReportMetadata(metadata);
        if (!processes.HasResult || !threads.HasResult || !scheduling.HasResult)
            throw new ReaderFailure(ReaderStatus.UnsupportedOrMissingData);
        Target[] targets = ResolveTargets(processes.Result, threads.Result, processId, threadIds);
        Analyze(scheduling.Result, stacks.HasResult ? stacks.Result : null, targets);
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

    private static void Analyze(ICpuSchedulingDataSource data, IStackDataSource? stacks, Target[] targets)
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
        AnalyzeStacks(data, stacks, byId);
    }

    private static void AnalyzeStacks(ICpuSchedulingDataSource scheduling, IStackDataSource? stacks, Dictionary<uint, Target> byId)
    {
        Console.WriteLine($"NATIVE_TYPED_CPU stackApiAvailable=1 stackDataAvailable={(stacks is null ? 0 : 1)} switchOutDirectStackApiAvailable=0 stackImageLoadCaptureRequested=0 symbolsRequested=0 stackLookupLimitPerThread=4096 stackFrameLimit=512 modulePresenceIsCausality=0 stackEventAssociationUnknown=1");
        foreach (IContextSwitch contextSwitch in scheduling.ContextSwitches)
        {
            IContextSwitchOut switchedOut = contextSwitch.SwitchOut;
            if (!byId.TryGetValue(unchecked((uint)switchedOut.ThreadId), out Target? target))
                continue;
            target.ValidateIdentity(switchedOut.Thread, switchedOut.Process);
            target.Stacks.Observe(stacks, contextSwitch, target);
        }
    }

    internal sealed class StackTotals
    {
        internal const int MaximumLookups = 4096;
        internal const int MaximumFrames = 512;
        private const int NtoskrnlBit = 1;
        private const int NtfsBit = 2;
        private const int FltmgrBit = 4;
        private const int StorportBit = 8;
        private const int OtherBit = 16;
        private long switchOutRecords, boundedOut, lifetimeUnknown, lifetimeRejected, exitBoundaryUnknown;
        private long missing, known, identityRejected, timestampRejected, empty, framesBoundedOut;
        private long imageMissing, imageNameMissing, ntoskrnl, ntfs, fltmgr, storport, other;

        internal void Observe(IStackDataSource? data, IContextSwitch contextSwitch, Target target)
        {
            switchOutRecords++;
            if (switchOutRecords > MaximumLookups)
            {
                boundedOut++;
                return;
            }
            TraceTimestamp timestamp = contextSwitch.Timestamp;
            int lifetime = target.StackLifetime(timestamp);
            if (lifetime == 0) { lifetimeUnknown++; return; }
            if (lifetime < 0) { lifetimeRejected++; return; }
            if (target.StackExitBoundaryUnknown) exitBoundaryUnknown++;
            if (data is null) { missing++; return; }
            // The SDK lookup is keyed by timestamp, TID and processor.
            // Switch-in and readying-thread stacks describe different execution contexts.
            IStackSnapshot? stack = data.GetStack(timestamp, target.Thread.Id, contextSwitch.Processor);
            if (stack is null) { missing++; return; }
            if (stack.ThreadId != target.Thread.Id || stack.ProcessId != target.Thread.ProcessId)
            {
                identityRejected++;
                return;
            }
            target.ValidateIdentity(stack.Thread, stack.Process);
            if (!stack.Timestamp.HasValue || stack.Timestamp.RelativeTimestamp != timestamp.RelativeTimestamp
                || stack.Processor != contextSwitch.Processor)
            {
                timestampRejected++;
                return;
            }
            // This is an exact lookup match, not proof of the source event type or causality.
            if (stack.Frames.Count == 0) { empty++; return; }
            known++;
            int mask = 0;
            int frameCount = Math.Min(stack.Frames.Count, MaximumFrames);
            framesBoundedOut += stack.Frames.Count - frameCount;
            for (int i = 0; i < frameCount; i++)
            {
                IImage? image = stack.Frames[i].Image;
                if (image is null) { imageMissing++; continue; }
                string? name = image.FileName;
                if (string.IsNullOrEmpty(name)) { imageNameMissing++; continue; }
                // Only compare module names in memory; never print a filename, path or frame.
                mask |= name.Equals("ntoskrnl.exe", StringComparison.OrdinalIgnoreCase) ? NtoskrnlBit
                    : name.Equals("ntfs.sys", StringComparison.OrdinalIgnoreCase) ? NtfsBit
                    : name.Equals("fltmgr.sys", StringComparison.OrdinalIgnoreCase) ? FltmgrBit
                    : name.Equals("storport.sys", StringComparison.OrdinalIgnoreCase) ? StorportBit : OtherBit;
            }
            if ((mask & NtoskrnlBit) != 0) ntoskrnl++;
            if ((mask & NtfsBit) != 0) ntfs++;
            if ((mask & FltmgrBit) != 0) fltmgr++;
            if ((mask & StorportBit) != 0) storport++;
            if ((mask & OtherBit) != 0) other++;
        }

        internal void Report(uint processId, uint threadId)
        {
            Console.WriteLine($"NATIVE_TYPED_CPU pid={processId} tid={threadId} switchOutRecords={switchOutRecords} stackLookupBoundedOut={boundedOut} stackLifetimeUnknown={lifetimeUnknown} stackLifetimeRejected={lifetimeRejected} stackExitBoundaryUnknown={exitBoundaryUnknown} switchOutStackMissing={missing} switchOutStackKnown={known} stackIdentityRejected={identityRejected} stackTimestampRejected={timestampRejected} stackEmpty={empty}");
            Console.WriteLine($"NATIVE_TYPED_CPU pid={processId} tid={threadId} stackFramesBoundedOut={framesBoundedOut} stackFrameImageMissing={imageMissing} stackFrameImageNameMissing={imageNameMissing} stackModuleNtoskrnl={ntoskrnl} stackModuleNtfs={ntfs} stackModuleFltmgr={fltmgr} stackModuleStorport={storport} stackModuleOther={other} stackCompletenessUnknown=1 stackModuleIdentityAuthenticityUnknown=1");
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
        internal StackTotals Stacks { get; } = new();
        internal bool StackExitBoundaryUnknown => !process.ExitTime.HasValue || !process.ExitTime.Value.HasValue
            || !Thread.ExitTime.HasValue || !Thread.ExitTime.Value.HasValue;

        internal int StackLifetime(TraceTimestamp timestamp)
        {
            if (!timestamp.HasValue || !process.CreateTime.HasValue || !process.CreateTime.Value.HasValue
                || !Thread.CreateTime.HasValue || !Thread.CreateTime.Value.HasValue)
                return 0;
            // RelativeTimestamp remains usable when the SDK lacks a complete raw timestamp.
            if (timestamp.RelativeTimestamp < process.CreateTime.Value.RelativeTimestamp
                || timestamp.RelativeTimestamp < Thread.CreateTime.Value.RelativeTimestamp
                || (process.ExitTime.HasValue && process.ExitTime.Value.HasValue
                    && timestamp.RelativeTimestamp > process.ExitTime.Value.RelativeTimestamp)
                || (Thread.ExitTime.HasValue && Thread.ExitTime.Value.HasValue
                    && timestamp.RelativeTimestamp > Thread.ExitTime.Value.RelativeTimestamp))
                return -1;
            return 1;
        }

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
            Stacks.Report(processId, tid);
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