using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Disk;
using Microsoft.Windows.EventTracing.Processes;

namespace TraceFileReader;

internal static class Program
{
    private const long MaximumInputBytes = 256L * 1024 * 1024;
    private const int MaximumThreads = 64;
    private const int InvalidInput = 2;
    private const int Unsupported = 3;
    private const int MissingData = 4;
    private const int NativeUnavailable = 5;
    private const int ParseFailure = 6;
    private const int LostData = 7;
    private const int AmbiguousIdentity = 8;
    private const int NoTargetObservation = 9;
    private const string Prefix = "NATIVE_TYPED_FILE";

    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        bool processing = false;
        try
        {
            if (!TryParse(args, out uint processId, out uint[] threadIds))
                return Finish(InvalidInput);

            int status;
            // Hold the input read-only so it cannot change while the native reader opens it.
            using (FileStream input = new(args[0], FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length is <= 0 or > MaximumInputBytes)
                    return Finish(InvalidInput);
                if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                    return Finish(Unsupported);

                Line($"input_bytes={input.Length} requested_threads={threadIds.Length}");
                Line("allow_lost_events=0 allow_time_inversion=0 symbols_requested=0");
                Line("syscall_data_available=0 machine_disk_io_used=0 capture_window_completeness_unknown=1");
                Line("raw_unpaired_events_available=0 duration_sum_is_wallclock_partition=0");
                processing = true;
                status = Read(args[0], processId, threadIds);
            }
            return Finish(status);
        }
        catch (TraceLostEventsException exception)
        {
            return Finish(LostData, exception.HResult);
        }
        catch (TraceTimeInversionException exception)
        {
            return Finish(ParseFailure, exception.HResult);
        }
        catch (InvalidTraceFormatException exception)
        {
            return Finish(Unsupported, exception.HResult);
        }
        catch (InvalidTraceDataException exception)
        {
            return Finish(ParseFailure, exception.HResult);
        }
        catch (Exception exception) when (processing && IsNativeFailure(exception))
        {
            return Finish(NativeUnavailable, exception.HResult);
        }
        catch (Exception exception) when (!processing && exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Finish(InvalidInput, exception.HResult);
        }
        catch (Exception exception)
        {
            return Finish(ParseFailure, exception.HResult);
        }
    }

    private static bool TryParse(string[] args, out uint processId, out uint[] threadIds)
    {
        processId = 0;
        threadIds = [];
        if (args.Length != 3 || !File.Exists(args[0]) ||
            !uint.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out processId) || processId == 0)
            return false;

        // Bound the token count before allocating the requested identity array.
        int count = 1;
        foreach (char character in args[2])
        {
            if (character == ',' && ++count > MaximumThreads)
                return false;
        }

        threadIds = new uint[count];
        ReadOnlySpan<char> remaining = args[2];
        for (int index = 0; index < count; index++)
        {
            int separator = remaining.IndexOf(',');
            ReadOnlySpan<char> token = separator < 0 ? remaining : remaining[..separator];
            if (!uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0)
                return false;
            for (int previous = 0; previous < index; previous++)
            {
                if (threadIds[previous] == id)
                    return false;
            }
            threadIds[index] = id;
            remaining = separator < 0 ? [] : remaining[(separator + 1)..];
        }
        return true;
    }

    private static int Read(string path, uint processId, uint[] threadIds)
    {
        string toolkit = Path.Combine(AppContext.BaseDirectory, "wpt");
        if (!File.Exists(Path.Combine(toolkit, "perfcore.dll")) ||
            !File.Exists(Path.Combine(toolkit, "perf_nt.dll")) ||
            !Directory.Exists(Path.Combine(toolkit, "Catalog")))
            return NativeUnavailable;
        var settings = new TraceProcessorSettings
        {
            AllowLostEvents = false,
            AllowTimeInversion = false,
            SuppressFirstTimeSetupMessage = true,
            ToolkitPath = toolkit,
            CatalogPath = Path.Combine(toolkit, "Catalog")
        };
        using ITraceProcessor processor = TraceProcessor.Create(path, settings);
        var metadata = processor.UseMetadata();
        var pendingProcesses = processor.UseProcesses();
        var pendingThreads = processor.UseThreads();
        var pendingFiles = processor.UseFileIOData();
        processor.Process();

        Line($"lost_events={metadata.LostEventCount} lost_buffers={metadata.LostBufferCount}");
        if (metadata.LostEventCount != 0 || metadata.LostBufferCount != 0)
            return LostData;
        if (!pendingProcesses.HasResult || !pendingThreads.HasResult || !pendingFiles.HasResult)
            return MissingData;

        var indices = new Dictionary<uint, int>(threadIds.Length);
        for (int index = 0; index < threadIds.Length; index++)
            indices.Add(threadIds[index], index);
        IProcess? targetProcess = null;
        int processMatches = 0;
        foreach (IProcess process in pendingProcesses.Result.Processes)
        {
            if (unchecked((uint)process.Id) == processId)
            {
                targetProcess = process;
                processMatches++;
            }
        }
        Line($"target_process_identity_count={processMatches}");
        if (processMatches > 1)
            return AmbiguousIdentity;
        if (targetProcess is null)
            return NoTargetObservation;

        var targets = new IThread?[threadIds.Length];
        var identityCounts = new int[threadIds.Length];
        foreach (IThread thread in pendingThreads.Result.Threads)
        {
            if (indices.TryGetValue(unchecked((uint)thread.Id), out int index))
            {
                targets[index] = thread;
                identityCounts[index]++;
            }
        }
        for (int index = 0; index < targets.Length; index++)
        {
            Line($"tid={threadIds[index]} identity_count={identityCounts[index]}");
            if (identityCounts[index] > 1)
                return AmbiguousIdentity;
            if (targets[index] is not IThread target)
                return NoTargetObservation;
            if (unchecked((uint)target.ProcessId) != processId ||
                target.Process is not IProcess owner || !SameProcess(owner, targetProcess))
                return AmbiguousIdentity;
            Line($"tid={threadIds[index]} create_boundary_known={Flag(target.CreateTime.HasValue)} exit_boundary_known={Flag(target.ExitTime.HasValue)}");
        }
        Line($"process_create_boundary_known={Flag(targetProcess.CreateTime.HasValue)} process_exit_boundary_known={Flag(targetProcess.ExitTime.HasValue)}");

        var context = new Attribution(targetProcess, targets, indices, threadIds);
        IFileActivityDataSource files = pendingFiles.Result;
        // The Disk namespace exposes separate typed file and disk sources; only the file source is used.
        context.Observe("create_open", files.CreateFileObjectActivity);
        context.Observe("directory_enumeration", files.EnumerateDirectoryActivity);
        context.Observe("query_information", files.QueryFileInformationActivity);
        context.Observe("read", files.ReadFileActivity);
        context.Observe("write", files.WriteFileActivity);
        context.Observe("directory_change_notification", files.DirectoryChangeNotificationActivity);
        context.Observe("change_capacity", files.ChangeFileCapacityActivity);
        context.Observe("cleanup", files.CleanupFileActivity);
        context.Observe("close", files.CloseFileActivity);
        context.Observe("delete_on_close", files.DeleteOnCloseFileActivity);
        context.Observe("flush", files.FlushFileActivity);
        context.Observe("rename", files.RenameFileActivity);
        context.Observe("resize", files.ResizeFileActivity);
        context.Observe("set_information", files.SetFileInformationActivity);
        context.Observe("file_system_control", files.FileSystemControlActivity);
        context.Observe("set_security", files.SetFileSecurityActivity);
        context.Observe("query_security", files.QueryFileSecurityActivity);
        context.Observe("set_extended_attributes", files.SetFileExtendedAttributesActivity);
        context.Observe("query_extended_attributes", files.QueryFileExtendedAttributesActivity);
        context.Emit();
        if (context.IdentityConflicts != 0)
            return AmbiguousIdentity;
        if (context.InvalidDurations != 0)
            return ParseFailure;
        if (context.TargetCount == 0)
            return context.AnyCategoryAvailable ? NoTargetObservation : MissingData;
        return 0;
    }

    private static bool SameKnownRelativeTime(TraceTimestamp? left, TraceTimestamp? right) =>
        left.HasValue && right.HasValue && left.Value.HasValue && right.Value.HasValue &&
        left.Value.RelativeTimestamp.Equals(right.Value.RelativeTimestamp);

    private static bool SameExitTime(TraceTimestamp? left, TraceTimestamp? right) =>
        left.HasValue == right.HasValue && (!left.HasValue || SameKnownRelativeTime(left, right));

    private static bool SameProcess(IProcess left, IProcess right) =>
        left.Id == right.Id && (ReferenceEquals(left, right) ||
            (SameKnownRelativeTime(left.CreateTime, right.CreateTime) &&
             SameExitTime(left.ExitTime, right.ExitTime)));

    private static bool SameThread(IThread left, IThread right) =>
        left.Id == right.Id && left.ProcessId == right.ProcessId &&
        (ReferenceEquals(left, right) ||
         (SameKnownRelativeTime(left.CreateTime, right.CreateTime) &&
          SameExitTime(left.ExitTime, right.ExitTime)));

    private static LifetimeBoundary CheckLifetime(TraceTimestamp timestamp, TraceTimestamp? create, TraceTimestamp? exit)
    {
        if (!timestamp.HasValue)
            return LifetimeBoundary.Within;
        Timestamp relative = timestamp.RelativeTimestamp;
        if (create.HasValue && relative < create.Value.RelativeTimestamp)
            return LifetimeBoundary.BeforeCreate;
        return exit.HasValue && relative > exit.Value.RelativeTimestamp
            ? LifetimeBoundary.AfterExit : LifetimeBoundary.Within;
    }

    private enum LifetimeBoundary { Within, BeforeCreate, AfterExit }

    private static bool IsNativeFailure(Exception exception) =>
        exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or
            FileNotFoundException or FileLoadException ||
        (exception is TypeInitializationException && exception.InnerException is Exception inner && IsNativeFailure(inner));

    private static int Flag(bool value) => value ? 1 : 0;

    private static void Line(string value) => Console.WriteLine($"{Prefix} {value}");

    private static int Finish(int status, int hresult = 0)
    {
        Line($"status={status} hresult={hresult}");
        return status;
    }

    private sealed class Attribution(
        IProcess process, IThread?[] threads, Dictionary<uint, int> indices, uint[] threadIds)
    {
        private readonly List<Category> categories = new(19);
        public long IdentityConflicts { get; private set; }
        public long InvalidDurations { get; private set; }
        public long TargetCount { get; private set; }
        public bool AnyCategoryAvailable { get; private set; }

        public void Observe<T>(string label, IReadOnlyList<T> activities) where T : IFileActivity
        {
            var category = new Category(label, activities.Count, threads.Length);
            categories.Add(category);
            if (activities.Count == 0)
                return;
            AnyCategoryAvailable = true;
            for (int activityIndex = 0; activityIndex < activities.Count; activityIndex++)
            {
                T activity = activities[activityIndex];
                IThread? issuingThread = activity.IssuingThread;
                IProcess? issuingProcess = activity.IssuingProcess;
                if (issuingThread is null)
                {
                    category.MissingIssuingThread++;
                    if (issuingProcess is not null && issuingProcess.Id == process.Id)
                        category.UnattributedTargetProcess++;
                    continue;
                }
                if (!indices.TryGetValue(unchecked((uint)issuingThread.Id), out int index))
                    continue;
                IThread? target = threads[index];
                IProcess? threadOwner = issuingThread.Process;
                int identityRejection = issuingProcess is null ? 0 :
                    !SameProcess(issuingProcess, process) ? 1 :
                    target is null ? 2 :
                    !SameThread(issuingThread, target) ? 3 :
                    threadOwner is null ? 4 :
                    !SameProcess(threadOwner, process) ? 5 : -1;
                if (identityRejection >= 0)
                {
                    category.MetadataRejections[identityRejection]++;
                    IdentityConflicts++;
                    continue;
                }
                IThread targetThread = target!;
                TraceTimestamp start = activity.StartTime;
                TraceTimestamp stop = activity.StopTime;
                bool processStart = category.RecordBoundary(0, CheckLifetime(start, process.CreateTime, process.ExitTime));
                bool processStop = category.RecordBoundary(1, CheckLifetime(stop, process.CreateTime, process.ExitTime));
                bool threadStart = category.RecordBoundary(2, CheckLifetime(start, targetThread.CreateTime, targetThread.ExitTime));
                bool threadStop = category.RecordBoundary(3, CheckLifetime(stop, targetThread.CreateTime, targetThread.ExitTime));
                if (processStart || processStop || threadStart || threadStop)
                {
                    category.LifetimeRejections++;
                    IdentityConflicts++;
                    continue;
                }
                if (start.HasValue && stop.HasValue && stop.RelativeTimestamp < start.RelativeTimestamp)
                {
                    InvalidDurations++;
                    continue;
                }
                category.Total.Add(start, stop);
                category.PerThread[index].Add(start, stop);
                TargetCount++;
            }
        }

        public void Emit()
        {
            Line($"target_observations={TargetCount} identity_conflicts={IdentityConflicts} invalid_durations={InvalidDurations}");
            foreach (Category category in categories)
            {
                // An empty SDK list does not prove that its event category was enabled in the capture.
                if (category.TraceCount == 0)
                {
                    Line($"category={category.Label} category_observed_in_trace=0 target_count_unknown=1");
                    continue;
                }
                Line($"category={category.Label} category_observed_in_trace=1 trace_observations={category.TraceCount} missing_issuing_thread={category.MissingIssuingThread} unattributed_target_process={category.UnattributedTargetProcess}");
                Line($"category={category.Label} rejected_metadata={category.MetadataRejections.Sum()} rejected_lifetime={category.LifetimeRejections}");
                Line($"category={category.Label} rejected_process_missing={category.MetadataRejections[0]} rejected_process_identity={category.MetadataRejections[1]} rejected_thread_missing={category.MetadataRejections[2]} rejected_thread_identity={category.MetadataRejections[3]} rejected_thread_owner_missing={category.MetadataRejections[4]} rejected_thread_owner_identity={category.MetadataRejections[5]}");
                Line($"category={category.Label} process_start_before_create={category.BoundaryRejections[0]} process_start_after_exit={category.BoundaryRejections[1]} process_stop_before_create={category.BoundaryRejections[2]} process_stop_after_exit={category.BoundaryRejections[3]}");
                Line($"category={category.Label} thread_start_before_create={category.BoundaryRejections[4]} thread_start_after_exit={category.BoundaryRejections[5]} thread_stop_before_create={category.BoundaryRejections[6]} thread_stop_after_exit={category.BoundaryRejections[7]}");
                EmitStats(category.Label, null, category.Total);
                for (int index = 0; index < threadIds.Length; index++)
                    EmitStats(category.Label, threadIds[index], category.PerThread[index]);
            }
        }

        private static void EmitStats(string category, uint? tid, Statistics stats)
        {
            string identity = tid.HasValue ? $" tid={tid.Value}" : "";
            Line($"category={category}{identity} target_observations={stats.Count} paired_endpoints={stats.Paired} missing_start={stats.MissingStart} missing_stop={stats.MissingStop} both_endpoints_missing={stats.BothMissing} duration_unknown={stats.DurationUnknown} partial_timestamp_pairs={stats.PartialPairs}");
            if (stats.Paired != 0)
                Line($"category={category}{identity} sdk_endpoint_duration_sum_seconds={stats.DurationSumSeconds} sdk_endpoint_duration_max_seconds={stats.DurationMaxSeconds}");
            else
                Line($"category={category}{identity} duration_sum_unknown=1 duration_max_unknown=1");
        }
    }

    private sealed class Category(string label, int traceCount, int threads)
    {
        public string Label { get; } = label;
        public int TraceCount { get; } = traceCount;
        public Statistics Total;
        public Statistics[] PerThread { get; } = new Statistics[threads];
        public long MissingIssuingThread;
        public long UnattributedTargetProcess;
        public long[] MetadataRejections { get; } = new long[6];
        public long[] BoundaryRejections { get; } = new long[8];
        public long LifetimeRejections;

        public bool RecordBoundary(int endpoint, LifetimeBoundary boundary)
        {
            if (boundary == LifetimeBoundary.Within)
                return false;
            BoundaryRejections[endpoint * 2 + (boundary == LifetimeBoundary.BeforeCreate ? 0 : 1)]++;
            return true;
        }
    }

    private struct Statistics
    {
        public long Count;
        public long Paired;
        public long MissingStart;
        public long MissingStop;
        public long BothMissing;
        public long DurationUnknown;
        public long PartialPairs;
        public decimal DurationSumSeconds;
        public decimal DurationMaxSeconds;

        public void Add(TraceTimestamp start, TraceTimestamp stop)
        {
            Count++;
            if (!start.HasValue)
                MissingStart++;
            if (!stop.HasValue)
                MissingStop++;
            if (!start.HasValue && !stop.HasValue)
                BothMissing++;
            if (!start.HasValue || !stop.HasValue)
            {
                DurationUnknown++;
                return;
            }
            if (start.IsPartial || stop.IsPartial)
                PartialPairs++;
            Paired++;
            decimal seconds = (stop.RelativeTimestamp - start.RelativeTimestamp).TotalSeconds;
            DurationSumSeconds += seconds;
            DurationMaxSeconds = Math.Max(DurationMaxSeconds, seconds);
        }
    }
}