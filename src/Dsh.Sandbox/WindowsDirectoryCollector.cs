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
    private const int WorkerCount = 256;
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
    /// Root entries are read and published before any descendant is opened.
    /// Up to 256 dedicated workers each own one 16 KiB buffer and normally one descendant handle.
    /// Root-relative names exceeding the native counted-name limit are split at directory separators.
    /// Such opens transiently own two descendant handles per worker; queued directories own none.
    /// Paths, entry topology and discovery/publication queues cost O(entries).
    /// All workers join before descendant publication; callbacks retain native entry and FIFO directory order.
    /// Native calls are synchronous, with cancellation checked before opens, between entries and after completion.
    /// </remarks>
    public static unsafe void Collect(string root, Action<string, bool> accept, CancellationToken cancellationToken,
        WindowsIoStageTiming.Apply? timing = null, WindowsIoStageTiming.Provenance provenance = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(accept);
        cancellationToken.ThrowIfCancellationRequested();
        var observation = WindowsIoStageTiming.BeginCollection(timing);
        try
        {
            var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var nativeRoot = rootPath.StartsWith(@"\\", StringComparison.Ordinal)
                && !rootPath.StartsWith(@"\\?\", StringComparison.Ordinal)
                ? $@"\\?\UNC\{rootPath.AsSpan(2)}" : rootPath;
            cancellationToken.ThrowIfCancellationRequested();
            var observationRoot = observation?.Root(0, provenance) ?? 0;
            observation?.Bind(0, observationRoot, 0);
            observation?.BeginDirectory(0, ReadOnlySpan<char>.Empty);
            SafeFileHandle rootHandle;
            int openError;
            observation?.Enter(0, WindowsIoStageTiming.Operation.Open);
            try
            {
                rootHandle = WindowsNative.CreateFile(nativeRoot, ListDirectory, ShareAll, 0, OpenExisting, BackupSemantics, 0);
                openError = Marshal.GetLastPInvokeError();
            }
            finally { observation?.Exit(0, WindowsIoStageTiming.Operation.Open); }
            try
            {
                if (rootHandle.IsInvalid)
                    throw DirectoryError(openError, rootPath, openingDirectory: true);
                var rootDirectory = new DirectoryWork(rootPath);
                var pending = new Queue<DirectoryWork>();
                var buffer = Marshal.AllocHGlobal(BufferSize);
                try
                {
                    CollectDirectory(rootHandle, rootDirectory, buffer, cancellationToken, observation: observation);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                    observation?.EndDirectory(0);
                }
                PublishDirectory(rootDirectory, pending, accept, cancellationToken);
                if (pending.Count != 0)
                {
                    var work = new WorkQueue(rootHandle, rootPath, pending, cancellationToken)
                    {
                        Observation = observation,
                        ObservationRoot = observationRoot
                    };
                    var workers = new Thread[WorkerCount];
                    var started = 0;
                    using var registration = cancellationToken.UnsafeRegister(static state =>
                    {
                        var queue = (WorkQueue)state!;
                        queue.Fail(new OperationCanceledException(queue.CancellationToken));
                    }, work);
                    try
                    {
                        for (; started < workers.Length; started++)
                        {
                            work.ThrowIfStopped();
                            var worker = new Thread(work.Run) { IsBackground = true };
                            workers[started] = worker;
                            worker.Start();
                        }
                    }
                    catch (Exception error)
                    {
                        work.Fail(error);
                    }
                    finally
                    {
                        for (var index = 0; index < started; index++)
                        {
                            while (true)
                            {
                                try
                                {
                                    observation?.Enter(0, WindowsIoStageTiming.Operation.WorkerJoin);
                                    try { workers[index].Join(); }
                                    finally { observation?.Exit(0, WindowsIoStageTiming.Operation.WorkerJoin); }
                                    break;
                                }
                                catch (ThreadInterruptedException error)
                                {
                                    work.Fail(error);
                                }
                            }
                        }
                    }
                    work.ThrowIfStopped();
                }
                observation?.RootCompleted(observationRoot);
                while (pending.TryDequeue(out var directory))
                {
                    PublishDirectory(directory, pending, accept, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally { Close(rootHandle, observation, 0); }
        }
        catch (Exception error)
        {
            observation?.Stopped(error is OperationCanceledException ? 1 : error is ThreadInterruptedException ? 2 : 3);
            throw;
        }
        finally
        {
            observation?.EndDirectory(0);
            observation?.Retire(0);
            observation?.Finished();
        }
    }

    private static unsafe void CollectDirectory(SafeFileHandle handle, DirectoryWork directory, nint buffer,
        CancellationToken cancellationToken, WorkQueue? work = null, int slot = 0,
        WindowsIoStageTiming.Collection? observation = null)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            work?.ThrowIfStopped();
            int status;
            IoStatusBlock ioStatus;
            observation?.Enter(slot, WindowsIoStageTiming.Operation.Query);
            try
            {
                status = NtQueryDirectoryFile(handle, 0, 0, 0, out ioStatus, buffer, BufferSize,
                    FileFullDirectoryInformation, 0, 0, 0);
            }
            finally { observation?.Exit(slot, WindowsIoStageTiming.Operation.Query); }
            observation?.QueryResult(status, ioStatus.Information, BufferSize);
            if (status is NoMoreFiles or NoSuchFile)
            {
                observation?.DirectoryCompleted(slot);
                return;
            }
            if (status != 0)
                throw DirectoryError((int)RtlNtStatusToDosError(status), directory.Path);
            if (ioStatus.Information == 0)
                return;
            var entry = (DirectoryInformation*)buffer;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                work?.ThrowIfStopped();
                observation?.ReturnedEntry(slot);
                var name = new ReadOnlySpan<char>(&entry->FileName, checked((int)entry->FileNameLength / sizeof(char)));
                if ((entry->Attributes & FileAttributes.ReparsePoint) == 0 && !name.SequenceEqual(".") && !name.SequenceEqual(".."))
                {
                    var path = Path.Join(directory.Path.AsSpan(), name);
                    var child = (entry->Attributes & FileAttributes.Directory) != 0 ? new DirectoryWork(path) : null;
                    directory.Entries.Add(new Entry(path, child));
                    observation?.EntryCollected(slot);
                }
                if (entry->NextEntryOffset == 0)
                    break;
                entry = (DirectoryInformation*)((byte*)entry + entry->NextEntryOffset);
            }
        }
    }

    private static void PublishDirectory(DirectoryWork directory, Queue<DirectoryWork> pending,
        Action<string, bool> accept, CancellationToken cancellationToken)
    {
        foreach (var entry in directory.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            accept(entry.Path, entry.Directory is not null);
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Directory is { } child)
                pending.Enqueue(child);
        }
    }

    private sealed class DirectoryWork(string path)
    {
        public string Path { get; } = path;
        public List<Entry> Entries { get; } = [];
    }

    private readonly record struct Entry(string Path, DirectoryWork? Directory);

    private sealed class WorkQueue
    {
        private readonly object _gate = new();
        private readonly SafeFileHandle _root;
        private readonly int _relativeStart;
        private readonly Queue<DirectoryWork> _pending;
        private int _outstanding;
        private ExceptionDispatchInfo? _failure;

        public CancellationToken CancellationToken { get; }
        public WindowsIoStageTiming.Collection? Observation { get; init; }
        public int ObservationRoot { get; init; }

        public WorkQueue(SafeFileHandle root, string rootPath, Queue<DirectoryWork> directories,
            CancellationToken cancellationToken)
        {
            _root = root;
            _relativeStart = rootPath.Length + (Path.EndsInDirectorySeparator(rootPath) ? 0 : 1);
            _pending = new Queue<DirectoryWork>(directories);
            _outstanding = _pending.Count;
            CancellationToken = cancellationToken;
        }

        public void Run()
        {
            nint buffer = 0;
            var slot = Observation?.Worker() ?? -1;
            try
            {
                buffer = Marshal.AllocHGlobal(BufferSize);
                while (Take(slot) is { } directory)
                {
                    ThrowIfStopped();
                    CancellationToken.ThrowIfCancellationRequested();
                    Observation?.Bind(slot, ObservationRoot, 0);
                    Observation?.BeginDirectory(slot, directory.Path.AsSpan(_relativeStart));
                    try
                    {
                        var handle = OpenRelativeDirectory(_root, directory.Path.AsSpan(_relativeStart), directory.Path, this, slot);
                        try { CollectDirectory(handle, directory, buffer, CancellationToken, this, slot, Observation); }
                        finally { Close(handle, Observation, slot); }
                    }
                    finally { Observation?.EndDirectory(slot); }
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
                Observation?.Retire(slot);
            }
        }

        public void ThrowIfStopped()
        {
            var failure = Volatile.Read(ref _failure);
            if (failure is null) return;
            var error = failure.SourceException;
            Observation?.Stopped(error is OperationCanceledException ? 1 : error is ThreadInterruptedException ? 2 : 3);
            failure.Throw();
        }

        public void Fail(Exception error)
        {
            lock (_gate)
            {
                if (_failure is not null)
                    return;
                Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(error));
                Monitor.PulseAll(_gate);
            }
        }

        private DirectoryWork? Take(int slot)
        {
            lock (_gate)
            {
                while (_failure is null && _pending.Count == 0 && _outstanding != 0)
                {
                    Observation?.Bind(slot, 0, -1);
                    Observation?.Enter(slot, WindowsIoStageTiming.Operation.QueueWait);
                    try { Monitor.Wait(_gate); }
                    finally { Observation?.Exit(slot, WindowsIoStageTiming.Operation.QueueWait); }
                }
                return _failure is not null || _outstanding == 0 ? null : _pending.Dequeue();
            }
        }

        private void Complete(DirectoryWork directory)
        {
            lock (_gate)
            {
                if (_failure is not null)
                    return;
                // Outstanding counts queued plus active nodes. Publish children before retiring their parent.
                foreach (var entry in directory.Entries)
                {
                    if (entry.Directory is not { } child)
                        continue;
                    _pending.Enqueue(child);
                    _outstanding = checked(_outstanding + 1);
                }
                _outstanding--;
                if (_outstanding == 0)
                    Observation?.RootCompleted(ObservationRoot);
                Monitor.PulseAll(_gate);
            }
        }
    }

    private static SafeFileHandle OpenRelativeDirectory(SafeFileHandle root, ReadOnlySpan<char> relativePath,
        string path, WorkQueue work, int slot)
    {
        const int maximumNameLength = ushort.MaxValue / sizeof(char);
        if (relativePath.Length <= maximumNameLength)
            return OpenDirectorySegment(root, relativePath, path, work.Observation, slot);

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
                var next = OpenDirectorySegment(current ?? root, relativePath[..segmentLength], path, work.Observation, slot);
                if (segmentLength == relativePath.Length)
                    return next;
                var previous = current;
                current = next;
                Close(previous, work.Observation, slot);
                relativePath = relativePath[(segmentLength + 1)..];
            }
        }
        finally
        {
            Close(current, work.Observation, slot);
        }
    }

    private static unsafe SafeFileHandle OpenDirectorySegment(SafeFileHandle parent, ReadOnlySpan<char> relativePath,
        string path, WindowsIoStageTiming.Collection? observation, int slot)
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
            int status;
            SafeFileHandle handle;
            observation?.Enter(slot, WindowsIoStageTiming.Operation.Open);
            try
            {
                status = NtOpenFile(out handle, ListDirectory | Synchronize, ref attributes, out _, ShareAll, SynchronousDirectoryOpen);
            }
            finally { observation?.Exit(slot, WindowsIoStageTiming.Operation.Open); }
            GC.KeepAlive(parent);
            if (status == 0)
                return handle;
            Close(handle, observation, slot);
            throw DirectoryError((int)RtlNtStatusToDosError(status), path, openingDirectory: true);
        }
    }

    private static void Close(SafeFileHandle? handle, WindowsIoStageTiming.Collection? observation, int slot)
    {
        if (handle is null) return;
        observation?.Enter(slot, WindowsIoStageTiming.Operation.CloseDispose);
        try { handle.Dispose(); }
        finally { observation?.Exit(slot, WindowsIoStageTiming.Operation.CloseDispose); }
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
