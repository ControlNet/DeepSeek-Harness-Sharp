using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

/// <summary>Collects non-reparse descendants using bounded root-relative native directory reads.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsDirectoryCollector
{
    private const int BufferSize = 16 * 1024;
    private const int WorkerCount = 255;
    private const int MaximumActiveRoots = 2;
    private const uint ListDirectory = 1;
    private const uint Synchronize = 0x00100000;
    private const uint BackupSemantics = 0x02000000;
    private const uint SynchronousDirectoryOpen = 0x00004021;
    private const int FileFullDirectoryInformation = 2;
    private const int NoMoreFiles = unchecked((int)0x80000006);
    private const int NoSuchFile = unchecked((int)0xc000000f);
    private const uint ShareAll = (uint)(FileShare.Read | FileShare.Write | FileShare.Delete);
    private const uint OpenExisting = 3;
    private const uint CaseInsensitive = 0x40;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorFilenameExceedsRange = 206;
    private const int ErrorOperationAborted = 995;
    private const int HResultHighWordMask = unchecked((int)0xffff0000);
    private const int Win32HResultMask = unchecked((int)0x80070000);

    /// <summary>
    /// Visits descendants, excluding the root, serially on the calling thread in native entry and FIFO directory order.
    /// No callback runs after this method returns or throws.
    /// Callers must finish collecting all roots before modifying any ACL.
    /// </summary>
    /// <remarks>
    /// Each root's entries are read and published on the caller before any of its descendants is opened.
    /// At most two root anchors and 255 demand-started, reusable dedicated workers own handles; every native buffer is 16 KiB.
    /// The caller scratch buffer and all worker buffers together are bounded by 4 MiB.
    /// Root-relative names exceeding the native counted-name limit are split at directory separators.
    /// Such opens transiently own two descendant handles per worker; queued directories own none.
    /// Paths, entry topology and discovery/publication queues cost O(entries).
    /// All workers join before descendant publication; callbacks retain native entry and FIFO directory order.
    /// Native calls are synchronous, with cancellation checked before opens, between entries and after completion.
    /// </remarks>
    public static void Collect(string root, Action<string, bool> accept, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(accept);
        Collect([new RootRequest(root, accept)], cancellationToken);
    }

    internal readonly record struct RootRequest(string Path, Action<string, bool> Accept);

    /// <summary>Collects independent trees with bounded root admission and preserves each root's publication order.</summary>
    public static void Collect(ReadOnlySpan<RootRequest> roots, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (roots.IsEmpty)
            return;
        var work = new WorkQueue(cancellationToken);
        var collected = new List<RootWork>(roots.Length);
        var active = new RootWork?[MaximumActiveRoots];
        nint buffer = 0;
        using var registration = cancellationToken.UnsafeRegister(static state =>
        {
            var queue = (WorkQueue)state!;
            queue.Fail(new OperationCanceledException(queue.CancellationToken));
        }, work);
        try
        {
            buffer = Marshal.AllocHGlobal(BufferSize);
            foreach (var request in roots)
            {
                work.ThrowIfStopped();
                cancellationToken.ThrowIfCancellationRequested();
                var slot = Array.IndexOf(active, null);
                if (slot < 0)
                {
                    slot = work.WaitForCompletedRoot(active);
                    active[slot]!.Handle!.Dispose();
                    active[slot]!.Handle = null;
                }
                work.ThrowIfStopped();
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(request.Path);
                ArgumentNullException.ThrowIfNull(request.Accept);
                var root = new RootWork(request);
                collected.Add(root);
                active[slot] = root;
                var nativeRoot = root.Directory.Path.StartsWith(@"\\", StringComparison.Ordinal)
                    && !root.Directory.Path.StartsWith(@"\\?\", StringComparison.Ordinal)
                    ? $@"\\?\UNC\{root.Directory.Path.AsSpan(2)}" : root.Directory.Path;
                root.Handle = WindowsNative.CreateFile(nativeRoot, ListDirectory, ShareAll, 0, OpenExisting, BackupSemantics, 0);
                if (root.Handle.IsInvalid)
                    throw DirectoryError(Marshal.GetLastPInvokeError(), root.Directory.Path, openingDirectory: true);
                CollectDirectory(root.Handle, root.Directory, buffer, cancellationToken, work);
                PublishDirectory(root.Directory, root.Pending, root.Accept, cancellationToken, work);
                work.Admit(root);
            }
        }
        catch (Exception error)
        {
            work.Fail(error);
        }
        finally
        {
            work.Seal();
            work.Join();
            foreach (var root in collected)
                root.Handle?.Dispose();
            if (buffer != 0)
                Marshal.FreeHGlobal(buffer);
        }
        work.ThrowIfStopped();
        foreach (var root in collected)
            while (root.Pending.TryDequeue(out var directory))
                PublishDirectory(directory, root.Pending, root.Accept, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static unsafe void CollectDirectory(SafeFileHandle handle, DirectoryWork directory, nint buffer,
        CancellationToken cancellationToken, WorkQueue? work = null)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            work?.ThrowIfStopped();
            var status = NtQueryDirectoryFile(handle, 0, 0, 0, out var ioStatus, buffer, BufferSize,
                FileFullDirectoryInformation, 0, 0, 0);
            if (!HasCompleteDirectoryBatch(status, ioStatus.Information, directory.Path))
                return;
            var entry = (DirectoryInformation*)buffer;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                work?.ThrowIfStopped();
                var name = new ReadOnlySpan<char>(&entry->FileName, checked((int)entry->FileNameLength / sizeof(char)));
                if ((entry->Attributes & FileAttributes.ReparsePoint) == 0 && !name.SequenceEqual(".") && !name.SequenceEqual(".."))
                {
                    var path = Path.Join(directory.Path.AsSpan(), name);
                    var child = (entry->Attributes & FileAttributes.Directory) != 0 ? new DirectoryWork(path, directory.Root) : null;
                    directory.Entries.Add(new Entry(path, child));
                }
                if (entry->NextEntryOffset == 0)
                    break;
                entry = (DirectoryInformation*)((byte*)entry + entry->NextEntryOffset);
            }
        }
    }

    internal static bool HasCompleteDirectoryBatch(int status, nuint byteCount, string path)
    {
        if (status is NoMoreFiles or NoSuchFile)
            return false;
        if (status != 0)
            throw DirectoryError((int)RtlNtStatusToDosError(status), path);
        return byteCount != 0;
    }

    private static void PublishDirectory(DirectoryWork directory, Queue<DirectoryWork> pending,
        Action<string, bool> accept, CancellationToken cancellationToken, WorkQueue? work = null)
    {
        foreach (var entry in directory.Entries)
        {
            work?.ThrowIfStopped();
            cancellationToken.ThrowIfCancellationRequested();
            accept(entry.Path, entry.Directory is not null);
            cancellationToken.ThrowIfCancellationRequested();
            work?.ThrowIfStopped();
            if (entry.Directory is { } child)
                pending.Enqueue(child);
        }
    }

    private sealed class RootWork
    {
        public RootWork(RootRequest request)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Path));
            Directory = new DirectoryWork(path, this);
            RelativeStart = path.Length + (Path.EndsInDirectorySeparator(path) ? 0 : 1);
            Accept = request.Accept;
        }

        public DirectoryWork Directory { get; }
        public int RelativeStart { get; }
        public Action<string, bool> Accept { get; }
        public Queue<DirectoryWork> Pending { get; } = new();
        public SafeFileHandle? Handle { get; set; }
        public int Outstanding { get; set; }
    }

    private sealed class DirectoryWork(string path, RootWork root)
    {
        public string Path { get; } = path;
        public RootWork Root { get; } = root;
        public List<Entry> Entries { get; } = [];
    }

    private readonly record struct Entry(string Path, DirectoryWork? Directory);

    private sealed class WorkQueue
    {
        private readonly object _gate = new();
        private readonly object _rootGate = new();
        private readonly Queue<DirectoryWork> _pending = new();
        private Thread[]? _workers;
        private ThreadStart? _run;
        private int _started;
        private int _waitingWorkers;
        private bool _waitingForRoot;
        private int _outstanding;
        private bool _sealed;
        private ExceptionDispatchInfo? _failure;

        public CancellationToken CancellationToken { get; }

        public WorkQueue(CancellationToken cancellationToken) => CancellationToken = cancellationToken;

        public void Admit(RootWork root)
        {
            lock (_gate)
            {
                ThrowIfStopped();
                foreach (var directory in root.Pending)
                    _pending.Enqueue(directory);
                root.Outstanding = root.Pending.Count;
                _outstanding = checked(_outstanding + root.Outstanding);
                GrowWorkers();
                WakeWorkers(root.Outstanding);
            }
        }

        public int WaitForCompletedRoot(RootWork?[] active)
        {
            // Only the admission caller waits here; worker pulses cannot wake the wrong predicate.
            lock (_rootGate)
                while (true)
                {
                    lock (_gate)
                    {
                        ThrowIfStopped();
                        for (var index = 0; index < active.Length; index++)
                            if (active[index]!.Outstanding == 0)
                                return index;
                        _waitingForRoot = true;
                    }
                    ThreadInterruptedException? interruption;
                    try
                    {
                        Monitor.Wait(_rootGate);
                    }
                    finally
                    {
                        interruption = EnterCleanupGate();
                        try
                        {
                            _waitingForRoot = false;
                        }
                        finally
                        {
                            Monitor.Exit(_gate);
                        }
                    }
                    if (interruption is not null)
                        throw interruption;
                }
        }

        public void Seal()
        {
            var interruption = EnterCleanupGate();
            try
            {
                if (interruption is not null && _failure is null)
                    Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(interruption));
                _sealed = true;
                if (_waitingWorkers != 0 && (_failure is not null || _outstanding == 0))
                    Monitor.PulseAll(_gate);
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        public void Join()
        {
            // Seal closes caller admission, not descendant discovery.
            // Re-read the published list after every join: a running worker may grow it, but no joined worker can start another worker.
            for (var index = 0; ; index++)
            {
                var interruption = EnterCleanupGate();
                Thread? worker;
                try
                {
                    worker = index < _started ? _workers![index] : null;
                }
                finally
                {
                    Monitor.Exit(_gate);
                }
                if (interruption is not null)
                    Fail(interruption);
                if (worker is null)
                    return;
                while (true)
                {
                    try
                    {
                        worker.Join();
                        break;
                    }
                    catch (ThreadInterruptedException error)
                    {
                        Fail(error);
                    }
                }
            }
        }

        public void Run()
        {
            nint buffer = 0;
            try
            {
                buffer = Marshal.AllocHGlobal(BufferSize);
                while (Take() is { } directory)
                {
                    ThrowIfStopped();
                    CancellationToken.ThrowIfCancellationRequested();
                    var root = directory.Root;
                    using (var handle = OpenRelativeDirectory(root.Handle!, directory.Path.AsSpan(root.RelativeStart), directory.Path, this))
                    {
                        CollectDirectory(handle, directory, buffer, CancellationToken, this);
                    }
                    Complete(directory);
                }
            }
            catch (Exception error)
            {
                Fail(error);
            }
            finally
            {
                if (buffer != 0)
                    Marshal.FreeHGlobal(buffer);
            }
        }

        public void ThrowIfStopped() => Volatile.Read(ref _failure)?.Throw();

        public void Fail(Exception error)
        {
            bool wakeRoot;
            EnterCleanupGate();
            try
            {
                if (_failure is not null)
                    return;
                Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(error));
                if (_waitingWorkers != 0)
                    Monitor.PulseAll(_gate);
                wakeRoot = _waitingForRoot;
            }
            finally
            {
                Monitor.Exit(_gate);
            }
            if (wakeRoot)
                WakeRootWaiter();
        }

        private ThreadInterruptedException? EnterCleanupGate() => EnterCleanupGate(_gate);

        private static ThreadInterruptedException? EnterCleanupGate(object gate)
        {
            ThreadInterruptedException? interruption = null;
            while (true)
            {
                try
                {
                    Monitor.Enter(gate);
                    return interruption;
                }
                catch (ThreadInterruptedException error)
                {
                    interruption ??= error;
                }
            }
        }

        private DirectoryWork? Take()
        {
            lock (_gate)
            {
                while (_failure is null && _pending.Count == 0 && (!_sealed || _outstanding != 0))
                {
                    _waitingWorkers++;
                    try
                    {
                        Monitor.Wait(_gate);
                    }
                    finally
                    {
                        _waitingWorkers--;
                    }
                }
                return _failure is not null || _pending.Count == 0 ? null : _pending.Dequeue();
            }
        }

        private void Complete(DirectoryWork directory)
        {
            bool rootCompleted;
            lock (_gate)
            {
                if (_failure is not null)
                    return;
                // Outstanding counts queued plus active nodes. Publish children before retiring their parent.
                var added = 0;
                foreach (var entry in directory.Entries)
                {
                    if (entry.Directory is not { } child)
                        continue;
                    _pending.Enqueue(child);
                    _outstanding = checked(_outstanding + 1);
                    directory.Root.Outstanding = checked(directory.Root.Outstanding + 1);
                    added++;
                }
                _outstanding--;
                directory.Root.Outstanding--;
                GrowWorkers();
                WakeWorkers(added);
                if (_sealed && _outstanding == 0 && _waitingWorkers != 0)
                    Monitor.PulseAll(_gate);
                rootCompleted = directory.Root.Outstanding == 0 && _waitingForRoot;
            }
            if (rootCompleted)
            {
                var interruption = WakeRootWaiter();
                if (interruption is not null)
                    Fail(interruption);
            }
        }

        private void GrowWorkers()
        {
            // Called only under _gate.
            // A successful Start and its list publication are indivisible to Join; a failed Start never enters the joinable prefix.
            // Sealing still permits growth.
            var target = Math.Min(WorkerCount, _outstanding);
            if (_started >= target)
                return;
            _workers ??= new Thread[WorkerCount];
            _run ??= Run;
            while (_started < target)
            {
                ThrowIfStopped();
                CancellationToken.ThrowIfCancellationRequested();
                var worker = new Thread(_run) { IsBackground = true };
                _workers[_started] = worker;
                worker.Start();
                _started++;
            }
        }

        private void WakeWorkers(int added)
        {
            // Only workers wait on _gate. Leaf completion does not change their work predicate.
            var count = Math.Min(added, _waitingWorkers);
            for (var index = 0; index < count; index++)
                Monitor.Pulse(_gate);
        }

        private ThreadInterruptedException? WakeRootWaiter()
        {
            // Never acquire _rootGate while holding _gate: admission checks use the opposite order.
            var interruption = EnterCleanupGate(_rootGate);
            try
            {
                Monitor.Pulse(_rootGate);
            }
            finally
            {
                Monitor.Exit(_rootGate);
            }
            return interruption;
        }
    }

    private static SafeFileHandle OpenRelativeDirectory(SafeFileHandle root, ReadOnlySpan<char> relativePath,
        string path, WorkQueue work)
    {
        const int maximumNameLength = ushort.MaxValue / sizeof(char);
        if (relativePath.Length <= maximumNameLength)
            return OpenDirectorySegment(root, relativePath, path);

        SafeFileHandle? current = null;
        try
        {
            while (true)
            {
                work.CancellationToken.ThrowIfCancellationRequested();
                work.ThrowIfStopped();
                var segmentLength = relativePath.Length;
                if (segmentLength > maximumNameLength)
                {
                    segmentLength = relativePath[..(maximumNameLength + 1)].LastIndexOfAny('\\', '/');
                    if (segmentLength <= 0)
                        throw new PathTooLongException($"Unable to open directory '{path}'.");
                }
                var next = OpenDirectorySegment(current ?? root, relativePath[..segmentLength], path);
                if (segmentLength == relativePath.Length)
                    return next;
                var previous = current;
                current = next;
                previous?.Dispose();
                relativePath = relativePath[(segmentLength + 1)..];
            }
        }
        finally
        {
            current?.Dispose();
        }
    }

    private static unsafe SafeFileHandle OpenDirectorySegment(SafeFileHandle parent, ReadOnlySpan<char> relativePath, string path)
    {
        fixed (char* text = relativePath)
        {
            var nativeName = new UnicodeString
            {
                Length = (ushort)(relativePath.Length * sizeof(char)),
                MaximumLength = (ushort)(relativePath.Length * sizeof(char)),
                Buffer = text
            };
            var attributes = new ObjectAttributes
            {
                Length = (uint)sizeof(ObjectAttributes),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = &nativeName,
                Attributes = CaseInsensitive
            };
            var status = NtOpenFile(out var handle, ListDirectory | Synchronize, ref attributes, out _, ShareAll, SynchronousDirectoryOpen);
            GC.KeepAlive(parent);
            if (status == 0)
                return handle;
            handle.Dispose();
            throw DirectoryError((int)RtlNtStatusToDosError(status), path, openingDirectory: true);
        }
    }

    private static Exception DirectoryError(int error, string path, bool openingDirectory = false)
    {
        // Like a nonrecursive FileSystemEnumerator root open, a missing directory is not a missing file.
        if (openingDirectory && error == ErrorFileNotFound)
            error = ErrorPathNotFound;
        var message = $"Unable to enumerate directory '{path}': {new Win32Exception(error).Message}";
        return error switch
        {
            ErrorFileNotFound => new FileNotFoundException(message, path),
            ErrorPathNotFound => new DirectoryNotFoundException(message),
            ErrorAccessDenied => new UnauthorizedAccessException(message),
            ErrorFilenameExceedsRange => new PathTooLongException(message),
            ErrorOperationAborted => new OperationCanceledException(message),
            _ => new IOException(message, (error & HResultHighWordMask) != 0 ? error : error | Win32HResultMask)
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DirectoryInformation
    {
        public uint NextEntryOffset, FileIndex;
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime, EndOfFile, AllocationSize;
        public FileAttributes Attributes;
        public uint FileNameLength, EaSize;
        public char FileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor, SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(SafeFileHandle handle, nint completionEvent, nint apcRoutine,
        nint apcContext, out IoStatusBlock statusBlock, nint information, uint length, int informationClass,
        byte returnSingleEntry, nint fileName, byte restartScan);

    [DllImport("ntdll.dll")]
    private static extern unsafe int NtOpenFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock statusBlock, uint share, uint options);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
