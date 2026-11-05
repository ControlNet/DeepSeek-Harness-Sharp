using System.Buffers;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

/// <summary>Collects non-reparse descendants using bounded native directory reads.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsDirectoryCollector
{
    private const int BufferSize = 16 * 1024;
    private const int WorkerCount = 255;
    private const int MaximumActiveRoots = 2;
    private const int MaximumAdditionalHandles = 511;
    private const int MaximumNativeNameLength = ushort.MaxValue / sizeof(char);
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
    private const int ErrorInsufficientBuffer = 122;
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
    /// At most two caller root anchors and 511 additional native handles are open together.
    /// Ordinary absolute-name opens reserve one descendant handle, peaking at 257 handles including root anchors.
    /// The captured resolved NT root name plus relative suffix must fit the native counted-name limit.
    /// Longer names use root-relative opens with two or three reserved handles.
    /// Segmented opens can reach the overall limit of 513 handles.
    /// Every native query buffer is 16 KiB, with caller and worker buffers together bounded by 4 MiB.
    /// Absolute names re-resolve the captured NT path, not later changes to the logical root alias or device mapping.
    /// All descendant and root self-open handles close before their reservation is returned; queued directories own none.
    /// Paths, entry topology and discovery/publication queues cost O(entries).
    /// All workers join before descendant publication; callbacks retain native entry and FIFO directory order.
    /// Native calls are synchronous, with cancellation checked before opens, between entries and after completion.
    /// </remarks>
    public static void Collect(string root, Action<string, bool> accept, CancellationToken cancellationToken,
        WindowsIoStageTiming.Apply? timing = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(accept);
        Collect([new RootRequest(root, accept)], cancellationToken, timing);
    }

    internal readonly record struct RootRequest(string Path, Action<string, bool> Accept);

    /// <summary>Collects independent trees with bounded root admission and preserves each root's publication order.</summary>
    public static void Collect(ReadOnlySpan<RootRequest> roots, CancellationToken cancellationToken,
        WindowsIoStageTiming.Apply? timing = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (roots.IsEmpty)
            return;
        var observation = WindowsIoStageTiming.BeginCollection(timing);
        var work = new WorkQueue(cancellationToken) { Observation = observation };
        var collected = new List<RootWork>(roots.Length);
        var active = new RootWork?[MaximumActiveRoots];
        Thread[]? workers = null;
        var started = 0;
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
                    observation?.Bind(0, active[slot]!.ObservationRoot, slot);
                    Close(active[slot]!.Handle, observation, 0);
                    active[slot]!.Handle = null;
                    active[slot]!.OpenedNativePath = null;
                }
                work.ThrowIfStopped();
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(request.Path);
                ArgumentNullException.ThrowIfNull(request.Accept);
                var root = new RootWork(request);
                collected.Add(root);
                active[slot] = root;
                root.ObservationRoot = observation?.Root(slot) ?? 0;
                root.AdmissionSlot = slot;
                observation?.Bind(0, root.ObservationRoot, slot);
                var path = root.Directory.Path;
                var devicePath = path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal);
                var nativeRoot = path.StartsWith(@"\\", StringComparison.Ordinal) && !devicePath ? $@"\\?\UNC\{path.AsSpan(2)}"
                    : path.StartsWith(@"\\.\", StringComparison.Ordinal) ? $@"\\?\{path.AsSpan(4)}" : path;
                observation?.Enter(0, WindowsIoStageTiming.Operation.Open);
                int openError;
                try
                {
                    root.Handle = WindowsNative.CreateFile(nativeRoot, ListDirectory, ShareAll, 0, OpenExisting, BackupSemantics, 0);
                    openError = Marshal.GetLastPInvokeError();
                }
                finally { observation?.Exit(0, WindowsIoStageTiming.Operation.Open); }
                if (root.Handle.IsInvalid)
                    throw DirectoryError(openError, root.Directory.Path, openingDirectory: true);
                CollectDirectory(root.Handle, root.Directory, buffer, cancellationToken, work);
                if (root.Directory.Entries.Exists(static entry => entry.Directory is not null))
                    root.OpenedNativePath = GetOpenedNativePath(root, observation);
                PublishDirectory(root.Directory, root.Pending, root.Accept, cancellationToken, work);
                work.Admit(root);
                if (root.Pending.Count != 0 && workers is null)
                {
                    workers = new Thread[WorkerCount];
                    for (; started < workers.Length; started++)
                    {
                        work.ThrowIfStopped();
                        var worker = new Thread(work.Run) { IsBackground = true };
                        workers[started] = worker;
                        worker.Start();
                    }
                }
            }
        }
        catch (Exception error)
        {
            work.Fail(error);
        }
        finally
        {
            work.Seal();
            for (var index = 0; index < started; index++)
                while (true)
                {
                    try
                    {
                        workers![index].Join();
                        break;
                    }
                    catch (ThreadInterruptedException error)
                    {
                        work.Fail(error);
                    }
                }
            foreach (var root in collected)
            {
                observation?.Bind(0, root.ObservationRoot, root.AdmissionSlot);
                Close(root.Handle, observation, 0);
                root.OpenedNativePath = null;
            }
            if (buffer != 0)
                Marshal.FreeHGlobal(buffer);
            observation?.Retire(0);
        }
        try
        {
            work.ThrowIfStopped();
            foreach (var root in collected)
                while (root.Pending.TryDequeue(out var directory))
                    PublishDirectory(directory, root.Pending, root.Accept, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally { observation?.Finished(); }
    }

    private static unsafe void CollectDirectory(SafeFileHandle handle, DirectoryWork directory, nint buffer,
        CancellationToken cancellationToken, WorkQueue? work = null, int slot = 0)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            work?.ThrowIfStopped();
            work?.Observation?.Enter(slot, WindowsIoStageTiming.Operation.Query);
            int status;
            IoStatusBlock ioStatus;
            try
            {
                status = NtQueryDirectoryFile(handle, 0, 0, 0, out ioStatus, buffer, BufferSize,
                    FileFullDirectoryInformation, 0, 0, 0);
            }
            finally { work?.Observation?.Exit(slot, WindowsIoStageTiming.Operation.Query); }
            if (!HasCompleteDirectoryBatch(status, ioStatus.Information, directory.Path))
            {
                work?.Observation?.DirectoryCompleted(slot);
                return;
            }
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
                    work?.Observation?.EntryCollected(slot);
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
        if (byteCount == 0)
            throw DirectoryError(ErrorInsufficientBuffer, path);
        return true;
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
        public string? OpenedNativePath { get; set; }
        public Action<string, bool> Accept { get; }
        public Queue<DirectoryWork> Pending { get; } = new();
        public SafeFileHandle? Handle { get; set; }
        public int Outstanding { get; set; }
        public int ObservationRoot { get; set; }
        public int AdmissionSlot { get; set; }
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
        private readonly Queue<DirectoryWork> _pending = new();
        private int _outstanding;
        private int _availableHandles = MaximumAdditionalHandles;
        private int _handleWaiters;
        private bool _sealed;
        private ExceptionDispatchInfo? _failure;

        public CancellationToken CancellationToken { get; }
        public WindowsIoStageTiming.Collection? Observation { get; init; }

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
                if (root.Outstanding == 0)
                    Observation?.RootCompleted(root.ObservationRoot);
                Monitor.PulseAll(_gate);
            }
        }

        public int WaitForCompletedRoot(RootWork?[] active)
        {
            lock (_gate)
                while (true)
                {
                    ThrowIfStopped();
                    for (var index = 0; index < active.Length; index++)
                        if (active[index]!.Outstanding == 0)
                            return index;
                    Observation?.Enter(0, WindowsIoStageTiming.Operation.RootAdmissionWait);
                    try { Monitor.Wait(_gate); }
                    finally { Observation?.Exit(0, WindowsIoStageTiming.Operation.RootAdmissionWait); }
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
                Monitor.PulseAll(_gate);
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        public void Run()
        {
            var slot = Observation?.Worker() ?? -1;
            nint buffer = 0;
            try
            {
                buffer = Marshal.AllocHGlobal(BufferSize);
                while (Take(slot) is { } directory)
                {
                    Observation?.Bind(slot, directory.Root.ObservationRoot, directory.Root.AdmissionSlot);
                    CollectReservedDirectory(directory, buffer, slot);
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

        private void CollectReservedDirectory(DirectoryWork directory, nint buffer, int slot)
        {
            var root = directory.Root;
            var relativePath = directory.Path.AsSpan(root.RelativeStart);
            var nativeRoot = root.OpenedNativePath;
            var absolute = nativeRoot is not null && relativePath.Length <= MaximumNativeNameLength - nativeRoot.Length;
            var handleCount = absolute ? 1 : relativePath.Length <= MaximumNativeNameLength ? 2 : 3;
            RentHandles(handleCount);
            try
            {
                ThrowIfStopped();
                CancellationToken.ThrowIfCancellationRequested();
                if (absolute)
                {
                    var handle = OpenAbsoluteDirectory(nativeRoot!, relativePath, directory.Path, Observation, slot);
                    try { CollectDirectory(handle, directory, buffer, CancellationToken, this, slot); }
                    finally { Close(handle, Observation, slot); }
                }
                else
                {
                    var self = OpenNamedDirectory(root.Handle!, ReadOnlySpan<char>.Empty, root.Directory.Path, Observation, slot);
                    try
                    {
                        var handle = OpenRelativeDirectory(self, relativePath, directory.Path, this, slot);
                        try { CollectDirectory(handle, directory, buffer, CancellationToken, this, slot); }
                        finally { Close(handle, Observation, slot); }
                    }
                    finally { Close(self, Observation, slot); }
                }
            }
            catch (Exception error)
            {
                Fail(error);
                throw;
            }
            finally
            {
                ReturnHandles(handleCount);
            }
            ThrowIfStopped();
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
            EnterCleanupGate();
            try
            {
                if (_failure is not null)
                    return;
                Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(error));
                Monitor.PulseAll(_gate);
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        private ThreadInterruptedException? EnterCleanupGate()
        {
            ThreadInterruptedException? interruption = null;
            while (true)
            {
                try
                {
                    Monitor.Enter(_gate);
                    return interruption;
                }
                catch (ThreadInterruptedException error)
                {
                    interruption ??= error;
                }
            }
        }

        private void RentHandles(int count)
        {
            lock (_gate)
            {
                while (true)
                {
                    ThrowIfStopped();
                    if (_availableHandles >= count)
                    {
                        _availableHandles -= count;
                        return;
                    }
                    _handleWaiters++;
                    try
                    {
                        Monitor.Wait(_gate);
                    }
                    finally
                    {
                        _handleWaiters--;
                    }
                }
            }
        }

        private void ReturnHandles(int count)
        {
            var interruption = EnterCleanupGate();
            try
            {
                _availableHandles += count;
                if (interruption is not null && _failure is null)
                {
                    Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(interruption));
                    Monitor.PulseAll(_gate);
                }
                else if (_handleWaiters != 0)
                    Monitor.PulseAll(_gate);
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        private DirectoryWork? Take(int slot)
        {
            lock (_gate)
            {
                while (_failure is null && _pending.Count == 0 && (!_sealed || _outstanding != 0))
                {
                    Observation?.Bind(slot, 0, -1);
                    Observation?.Enter(slot, WindowsIoStageTiming.Operation.QueueWait);
                    try { Monitor.Wait(_gate); }
                    finally { Observation?.Exit(slot, WindowsIoStageTiming.Operation.QueueWait); }
                }
                return _failure is not null || _pending.Count == 0 ? null : _pending.Dequeue();
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
                    directory.Root.Outstanding = checked(directory.Root.Outstanding + 1);
                }
                _outstanding--;
                directory.Root.Outstanding--;
                if (directory.Root.Outstanding == 0)
                    Observation?.RootCompleted(directory.Root.ObservationRoot);
                Monitor.PulseAll(_gate);
            }
        }
    }

    private static unsafe string? GetOpenedNativePath(RootWork root, WindowsIoStageTiming.Collection? observation)
    {
        const uint openedNtName = 0x8 | 0x2; // FILE_NAME_OPENED | VOLUME_NAME_NT; do not normalize SMB ancestors.
        // Capture the resolved NT name, not a namespace snapshot or an identity/permission proof.
        const int capacity = MaximumNativeNameLength + 1;
        var buffer = ArrayPool<char>.Shared.Rent(capacity);
        try
        {
            fixed (char* text = buffer)
            {
                observation?.Enter(0, WindowsIoStageTiming.Operation.RootName);
                uint length;
                int error;
                try
                {
                    length = GetFinalPathNameByHandle(root.Handle!, text, capacity, openedNtName);
                    error = Marshal.GetLastPInvokeError();
                }
                finally { observation?.Exit(0, WindowsIoStageTiming.Operation.RootName); }
                if (length == 0)
                    throw DirectoryError(error, root.Directory.Path);
                // A required size beyond this buffer already rules out every absolute descendant.
                if (length >= capacity)
                    return null;
                var name = new ReadOnlySpan<char>(text, checked((int)length));
                return name[^1] == '\\' ? new string(name) : $"{name}\\";
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static SafeFileHandle OpenAbsoluteDirectory(ReadOnlySpan<char> nativeRoot, ReadOnlySpan<char> relativePath,
        string path, WindowsIoStageTiming.Collection? observation, int slot)
    {
        const int stackCharacters = 512;
        var length = nativeRoot.Length + relativePath.Length;
        char[]? rented = null;
        Span<char> name = length <= stackCharacters ? stackalloc char[length]
            : (rented = ArrayPool<char>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            nativeRoot.CopyTo(name);
            relativePath.CopyTo(name[nativeRoot.Length..]);
            return OpenNamedDirectory(null, name, path, observation, slot);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static SafeFileHandle OpenRelativeDirectory(SafeFileHandle root, ReadOnlySpan<char> relativePath,
        string path, WorkQueue work, int slot)
    {
        if (relativePath.Length <= MaximumNativeNameLength)
            return OpenNamedDirectory(root, relativePath, path, work.Observation, slot);

        SafeFileHandle? current = null;
        try
        {
            while (true)
            {
                work.CancellationToken.ThrowIfCancellationRequested();
                work.ThrowIfStopped();
                var segmentLength = relativePath.Length;
                if (segmentLength > MaximumNativeNameLength)
                {
                    segmentLength = relativePath[..(MaximumNativeNameLength + 1)].LastIndexOfAny('\\', '/');
                    if (segmentLength <= 0)
                        throw new PathTooLongException($"Unable to open directory '{path}'.");
                }
                var next = OpenNamedDirectory(current ?? root, relativePath[..segmentLength], path, work.Observation, slot);
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

    private static unsafe SafeFileHandle OpenNamedDirectory(SafeFileHandle? parent, ReadOnlySpan<char> name,
        string path, WindowsIoStageTiming.Collection? observation, int slot)
    {
        fixed (char* text = name)
        {
            var nativeName = new UnicodeString
            {
                Length = (ushort)(name.Length * sizeof(char)),
                MaximumLength = (ushort)(name.Length * sizeof(char)),
                Buffer = text
            };
            var attributes = new ObjectAttributes
            {
                Length = (uint)sizeof(ObjectAttributes),
                RootDirectory = parent?.DangerousGetHandle() ?? 0,
                ObjectName = &nativeName,
                Attributes = CaseInsensitive
            };
            observation?.Enter(slot, WindowsIoStageTiming.Operation.Open);
            int status;
            SafeFileHandle handle;
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

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern unsafe uint GetFinalPathNameByHandle(SafeFileHandle handle, char* path, uint capacity, uint flags);

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
