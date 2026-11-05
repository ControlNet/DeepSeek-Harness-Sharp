using System.ComponentModel;
using System.IO.Enumeration;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dsh.Sandbox;

namespace Dsh.Tests;

[SupportedOSPlatform("windows")]
[Trait("Category", "SandboxNative")]
public sealed class WindowsDirectoryCollectorTests : IDisposable
{
    private readonly string _root = TempTree.CreateDirectory("windows-directory-collector");

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void CompleteTree_PreservesRecursiveEnumerationAndStableGrantOrder()
    {
        RequireWindows();
        var expectedCount = PopulateTree();
        var hidden = Path.Combine(_root, "hidden-system.txt");
        File.WriteAllText(hidden, "included");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        expectedCount++;
        // Compare against the actual recursive implementation replaced at AddTree, not a second traversal algorithm.
        var expected = new FileSystemEnumerable<(string Path, bool Directory)>(_root,
            static (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
                BufferSize = 16 * 1024
            }).ToArray();
        var actual = new List<(string Path, bool Directory)>();
        var callerThread = Environment.CurrentManagedThreadId;

        WindowsDirectoryCollector.Collect(_root, (path, directory) =>
        {
            Assert.Equal(callerThread, Environment.CurrentManagedThreadId);
            actual.Add((path, directory));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(expectedCount, actual.Count);
        Assert.True(expected.SequenceEqual(actual), "Collection must retain native entry order and FIFO directory order.");
        Assert.Equal(actual.Count, actual.Select(static entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(actual, static entry => Assert.Equal(Directory.Exists(entry.Path), entry.Directory));
    }

    [Fact]
    public void NarrowThenWideTree_PreservesReferenceOrderThroughDescendantWorkerGrowth()
    {
        RequireWindows();
        PopulateNarrowThenWideTree(_root, 300);
        var expected = ReferenceEntries(_root);
        var actual = new List<(string Path, bool Directory)>();
        var caller = Environment.CurrentManagedThreadId;

        WindowsDirectoryCollector.Collect(_root, (path, directory) =>
        {
            Assert.Equal(caller, Environment.CurrentManagedThreadId);
            actual.Add((path, directory));
        }, TestContext.Current.CancellationToken);

        Assert.True(expected.SequenceEqual(actual),
            "A single-child frontier must not strand the wider frontier discovered after root admission.");
        AssertDirectoriesReleased();
    }

    [Fact]
    public void NarrowThenWideLockedDescendant_PreservesNativeFailureAndReleasesEveryHandle()
    {
        RequireWindows();
        var frontier = PopulateNarrowThenWideTree(_root, 300);
        var locked = Path.Combine(frontier, "branch-150", "nested");
        var actual = new List<string>();
        using (var handle = WindowsNative.CreateFile(locked, 1, 0, 0, 3, 0x02000000, 0))
        {
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The exclusive wide-frontier fixture could not open its directory.");

            var failure = Assert.Throws<IOException>(() => WindowsDirectoryCollector.Collect(_root,
                (path, _) => actual.Add(path), TestContext.Current.CancellationToken));

            Assert.Equal(unchecked((int)0x80070020), failure.HResult);
            Assert.Equal(new[] { Path.Combine(_root, "level-0") }, actual);
        }
        AssertDirectoriesReleased();
    }

    [Fact]
    public void ReparseEntries_AreExcludedWithoutFollowingExternalTargetsOrCycles()
    {
        RequireWindows();
        var tree = Directory.CreateDirectory(Path.Combine(_root, "tree")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(tree, "nested")).FullName;
        var input = Path.Combine(nested, "input.txt");
        File.WriteAllText(input, "included");
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "not-in-tree.txt"), "excluded");
        var externalLink = Path.Combine(tree, "external");
        var cycleLink = Path.Combine(tree, "cycle");
        var fileLink = Path.Combine(tree, "alias.txt");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(externalLink, outside);
                Directory.CreateSymbolicLink(cycleLink, tree);
                File.CreateSymbolicLink(fileLink, input);
            }
            catch (IOException error) when ((error.HResult & 0xffff) == 1314)
            {
                Assert.Skip("Creating Windows symbolic links requires Developer Mode or the symbolic-link privilege.");
            }

            var actual = new List<(string Path, bool Directory)>();
            WindowsDirectoryCollector.Collect(tree, (path, directory) => actual.Add((path, directory)), TestContext.Current.CancellationToken);

            Assert.True(actual.SequenceEqual(new[] { (nested, true), (input, false) }),
                "Only real descendants, not reparse entries or their targets, belong to the collected topology.");
        }
        finally
        {
            // Remove links before TempTree's recursive cleanup so the cycle is never traversed by the fixture.
            File.Delete(fileLink);
            if (Directory.Exists(cycleLink))
                Directory.Delete(cycleLink);
            if (Directory.Exists(externalLink))
                Directory.Delete(externalLink);
        }
    }

    [Fact]
    public void PreCanceledCollection_DoesNotOpenMissingRootOrVisitEntries()
    {
        RequireWindows();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var visits = 0;

        var error = Assert.ThrowsAny<OperationCanceledException>(() => WindowsDirectoryCollector.Collect(
            Path.Combine(_root, "missing"), (_, _) => visits++, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, visits);
    }

    [Fact]
    public void CancellationFromVisitor_StopsPublicationBeforeTheNextEntry()
    {
        RequireWindows();
        PopulateNarrowThenWideTree(_root, 300);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var files = 0;

        var error = Assert.ThrowsAny<OperationCanceledException>(() => WindowsDirectoryCollector.Collect(_root, (_, directory) =>
        {
            if (directory)
                return;
            files++;
            cancellation.Cancel();
        }, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, files);
        AssertDirectoriesReleased();
    }

    [Fact]
    public void CancellationFromFinalVisitor_IsReportedAfterTheLastEntry()
    {
        RequireWindows();
        var file = Path.Combine(_root, "only.txt");
        File.WriteAllText(file, "included");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var visits = 0;

        var error = Assert.ThrowsAny<OperationCanceledException>(() => WindowsDirectoryCollector.Collect(_root, (path, directory) =>
        {
            Assert.Equal(file, path);
            Assert.False(directory);
            visits++;
            cancellation.Cancel();
        }, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, visits);
    }

    [Fact]
    public void VisitorFailure_IsPreservedAndStopsPublication()
    {
        RequireWindows();
        PopulateTree();
        var failure = new InvalidOperationException("Visitor rejected the collected entry.");
        var files = 0;

        var observed = Assert.Throws<InvalidOperationException>(() => WindowsDirectoryCollector.Collect(_root, (_, directory) =>
        {
            if (directory)
                return;
            files++;
            throw failure;
        }, TestContext.Current.CancellationToken));

        Assert.Same(failure, observed);
        Assert.Equal(1, files);
        AssertDirectoriesReleased();
    }

    [Fact]
    public void RootVisitorFailure_IsPreservedBeforeDescendantIo()
    {
        RequireWindows();
        var child = Directory.CreateDirectory(Path.Combine(_root, "locked")).FullName;
        var failure = new InvalidOperationException("Visitor rejected the root entry.");
        var visits = 0;
        using (var handle = WindowsNative.CreateFile(child, 1, 0, 0, 3, 0x02000000, 0))
        {
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The exclusive directory fixture could not open its directory.");

            var observed = Assert.Throws<InvalidOperationException>(() => WindowsDirectoryCollector.Collect(_root, (path, directory) =>
            {
                Assert.Equal(child, path);
                Assert.True(directory);
                visits++;
                throw failure;
            }, TestContext.Current.CancellationToken));

            Assert.Same(failure, observed);
            Assert.Equal(1, visits);
        }
        AssertDirectoriesReleased();
    }

    [Fact]
    public void MissingRoot_IsReportedWithoutPublishingEntries()
    {
        RequireWindows();
        var visits = 0;

        Assert.Throws<DirectoryNotFoundException>(() => WindowsDirectoryCollector.Collect(
            Path.Combine(_root, "missing"), (_, _) => visits++, TestContext.Current.CancellationToken));

        Assert.Equal(0, visits);
    }

    [Fact]
    public void DiscoveredDirectoryRemovedByVisitor_IsReportedRatherThanSilentlySkipped()
    {
        RequireWindows();
        var child = Directory.CreateDirectory(Path.Combine(_root, "child")).FullName;
        File.WriteAllText(Path.Combine(child, "input.txt"), "removed before child enumeration");
        var visits = 0;

        Assert.Throws<DirectoryNotFoundException>(() => WindowsDirectoryCollector.Collect(_root, (path, directory) =>
        {
            visits++;
            Assert.True(directory && string.Equals(child, path, StringComparison.Ordinal));
            Directory.Delete(child, recursive: true);
        }, TestContext.Current.CancellationToken));

        Assert.Equal(1, visits);
    }

    [Fact]
    public void DiscoveredDirectoryReplacedByFile_IsReportedRatherThanSilentlySkipped()
    {
        RequireWindows();
        var child = Directory.CreateDirectory(Path.Combine(_root, "child")).FullName;
        var visits = 0;

        var failure = Assert.Throws<IOException>(() => WindowsDirectoryCollector.Collect(_root, (path, directory) =>
        {
            visits++;
            Assert.True(directory && string.Equals(child, path, StringComparison.Ordinal));
            Directory.Delete(child);
            File.WriteAllText(child, "no longer a directory");
        }, TestContext.Current.CancellationToken));

        Assert.Equal(unchecked((int)0x8007010b), failure.HResult);
        Assert.Equal(1, visits);
    }

    [Fact]
    public void ExclusivelyOpenedNestedDirectory_IsReportedAndReleasesAncestors()
    {
        RequireWindows();
        var child = Directory.CreateDirectory(Path.Combine(_root, "child")).FullName;
        var middle = Directory.CreateDirectory(Path.Combine(child, "middle")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(middle, "locked")).FullName;
        var file = Path.Combine(nested, "input.txt");
        File.WriteAllText(file, "not enumerable");
        var actual = new List<string>();
        const uint listDirectory = 1;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        const int sharingViolation = unchecked((int)0x80070020);
        using (var handle = WindowsNative.CreateFile(nested, listDirectory, 0, 0, openExisting, backupSemantics, 0))
        {
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The exclusive nested directory fixture could not open its directory.");

            var failure = Assert.Throws<IOException>(() => WindowsDirectoryCollector.Collect(_root,
                (path, _) => actual.Add(path), TestContext.Current.CancellationToken));

            Assert.Equal(sharingViolation, failure.HResult);
            Assert.DoesNotContain(file, actual);
        }
        AssertDirectoriesReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExclusivelyOpenedDirectory_IsReportedWithoutPublishingItsEntries(bool collectParent)
    {
        RequireWindows();
        var directory = Directory.CreateDirectory(Path.Combine(_root, "locked"));
        File.WriteAllText(Path.Combine(directory.FullName, "input.txt"), "not enumerable");
        const uint listDirectory = 1;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        const int sharingViolation = unchecked((int)0x80070020);
        using var handle = WindowsNative.CreateFile(directory.FullName, listDirectory, 0, 0, openExisting, backupSemantics, 0);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The exclusive directory fixture could not open its directory.");
        var visits = 0;

        var failure = Assert.Throws<IOException>(() => WindowsDirectoryCollector.Collect(collectParent ? _root : directory.FullName,
            (path, isDirectory) =>
            {
                Assert.Equal(directory.FullName, path);
                Assert.True(isDirectory);
                visits++;
            }, TestContext.Current.CancellationToken));

        Assert.Equal(sharingViolation, failure.HResult);
        Assert.Equal(collectParent ? 1 : 0, visits);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(65)]
    public void DeepTree_PreservesReferenceOrderAndReleasesParentHandles(int depth)
    {
        RequireWindows();
        var directory = _root;
        for (var index = 0; index < depth; index++)
        {
            directory = Directory.CreateDirectory(Path.Combine(directory, "d")).FullName;
            File.WriteAllText(Path.Combine(directory, "input.txt"), "included");
            if (index % 8 == 0)
            {
                var sibling = Directory.CreateDirectory(Path.Combine(directory, "side")).FullName;
                File.WriteAllText(Path.Combine(sibling, "input.txt"), "included");
            }
        }
        var expected = new FileSystemEnumerable<(string Path, bool Directory)>(_root,
            static (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
                BufferSize = 16 * 1024
            }).ToArray();
        var actual = new List<(string Path, bool Directory)>();
        var callerThread = Environment.CurrentManagedThreadId;

        WindowsDirectoryCollector.Collect(_root, (path, isDirectory) =>
        {
            Assert.Equal(callerThread, Environment.CurrentManagedThreadId);
            actual.Add((path, isDirectory));
        }, TestContext.Current.CancellationToken);

        Assert.True(expected.SequenceEqual(actual), "Deep branches must retain reference entry and FIFO directory order.");
        AssertDirectoriesReleased();
    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(65)]
    public void ExclusivelyOpenedDeepDirectory_IsReportedAndReleasesParentHandles(int depth)
    {
        RequireWindows();
        var directory = _root;
        for (var index = 0; index < depth; index++)
            directory = Directory.CreateDirectory(Path.Combine(directory, "d")).FullName;
        var file = Path.Combine(directory, "input.txt");
        File.WriteAllText(file, "not enumerable");
        var actual = new List<string>();
        const int sharingViolation = unchecked((int)0x80070020);
        using (var handle = WindowsNative.CreateFile(directory, 1, 0, 0, 3, 0x02000000, 0))
        {
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The exclusive deep directory fixture could not open its directory.");

            var failure = Assert.Throws<IOException>(() => WindowsDirectoryCollector.Collect(_root,
                (path, _) => actual.Add(path), TestContext.Current.CancellationToken));

            Assert.Equal(sharingViolation, failure.HResult);
            Assert.DoesNotContain(file, actual);
        }
        AssertDirectoriesReleased();
    }

    [Fact]
    public void InterruptedSealing_RetiresWaitingWorkersBeforeFailureIsPropagated()
    {
        RequireWindows();
        Assert.IsType<ThreadInterruptedException>(RunInterruptedCleanup(null));
    }

    [Fact]
    public void InterruptedFailureCleanup_RetiresWorkersAndPreservesThePrimaryFailure()
    {
        RequireWindows();
        var failure = new IOException("The native operation failed before cleanup.");
        Assert.Same(failure, RunInterruptedCleanup(failure));
    }

    private static Exception? RunInterruptedCleanup(Exception? primaryFailure)
    {
        const int threadDeadlineSeconds = 5;
        var deadline = TimeSpan.FromSeconds(threadDeadlineSeconds);
        var queueType = typeof(WindowsDirectoryCollector).GetNestedType("WorkQueue", BindingFlags.NonPublic)!;
        var queue = Activator.CreateInstance(queueType, [CancellationToken.None, null])!;
        var gate = queueType.GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(queue)!;
        var run = queueType.GetMethod("Run")!.CreateDelegate<Action>(queue);
        var seal = queueType.GetMethod("Seal")!.CreateDelegate<Action>(queue);
        var fail = queueType.GetMethod("Fail")!.CreateDelegate<Action<Exception>>(queue);
        var demand = queueType.GetMethod("ThrowIfStopped")!.CreateDelegate<Action>(queue);
        Exception? cleanupFailure = null;
        var worker = new Thread(run.Invoke) { IsBackground = true };
        var caller = new Thread(() =>
        {
            try
            {
                if (primaryFailure is null)
                    seal();
                else
                    fail(primaryFailure);
            }
            catch (Exception error)
            {
                cleanupFailure = error;
            }
        }) { IsBackground = true };
        try
        {
            worker.Start();
            lock (gate)
            {
                caller.Start();
                Assert.True(SpinWait.SpinUntil(
                    () => (caller.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, deadline),
                    "The cleanup caller must be waiting for the queue gate before interruption.");
                caller.Interrupt();
            }
            Assert.True(caller.Join(deadline), "Cleanup must finish after the gate is released.");
            Assert.True(worker.Join(deadline), "An interrupted cleanup must release its waiting worker.");
            Assert.Null(cleanupFailure);
            return Record.Exception(demand);
        }
        finally
        {
            fail(new OperationCanceledException());
            if (caller.IsAlive)
                Assert.True(caller.Join(deadline), "The owned cleanup caller must exit.");
            if (worker.IsAlive)
                Assert.True(worker.Join(deadline), "The owned worker must exit.");
        }
    }

    [Fact]
    public void IndependentRoots_PreserveEachReferenceOrderAndCallerPublicationAcrossAdmissionSlots()
    {
        RequireWindows();
        var caller = Environment.CurrentManagedThreadId;
        var roots = new string[4];
        var actual = new List<(string Path, bool Directory)>[roots.Length];
        var expected = new (string Path, bool Directory)[roots.Length][];
        var requests = new WindowsDirectoryCollector.RootRequest[roots.Length];
        var rootVisits = 0;
        for (var index = 0; index < roots.Length; index++)
        {
            var ordinal = index;
            roots[index] = Directory.CreateDirectory(Path.Combine(_root, $"root-{index}")).FullName;
            for (var branch = 0; branch < 3; branch++)
            {
                var child = Directory.CreateDirectory(Path.Combine(roots[index], $"child-{branch}")).FullName;
                var nested = Directory.CreateDirectory(Path.Combine(child, "nested")).FullName;
                File.WriteAllText(Path.Combine(nested, "input.txt"), "included");
            }
            expected[index] = new FileSystemEnumerable<(string Path, bool Directory)>(roots[index],
                static (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory),
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    IgnoreInaccessible = false,
                    BufferSize = 16 * 1024
                }).ToArray();
            actual[index] = [];
            requests[index] = new WindowsDirectoryCollector.RootRequest(roots[index], (path, directory) =>
            {
                Assert.Equal(caller, Environment.CurrentManagedThreadId);
                if (ordinal == 2 && actual[ordinal].Count == 0)
                {
                    var released = 0;
                    for (var prior = 0; prior < 2; prior++)
                    {
                        using var exclusive = WindowsNative.CreateFile(roots[prior], 1, 0, 0, 3, 0x02000000, 0);
                        if (!exclusive.IsInvalid)
                            released++;
                        else
                            Assert.Equal(32, Marshal.GetLastPInvokeError());
                    }
                    Assert.Equal(1, released);
                }
                if (Path.GetDirectoryName(path) == roots[ordinal])
                    rootVisits++;
                else
                    Assert.Equal(roots.Length * 3, rootVisits);
                actual[ordinal].Add((path, directory));
            });
        }

        WindowsDirectoryCollector.Collect(requests, TestContext.Current.CancellationToken);

        for (var index = 0; index < roots.Length; index++)
            Assert.True(expected[index].SequenceEqual(actual[index]),
                "Every admitted root must retain its complete native entry and FIFO directory order.");
        AssertDirectoriesReleased();
    }

    [Fact]
    public void EmptyAndNarrowThenWideRoots_PreserveOrderAndReleaseHandlesAcrossWorkerReuse()
    {
        RequireWindows();
        int[] widths = [0, 1, 280, 0, 3, 300];
        var requests = new WindowsDirectoryCollector.RootRequest[widths.Length];
        var expected = new (string Path, bool Directory)[widths.Length][];
        var actual = new List<(string Path, bool Directory)>[widths.Length];
        var caller = Environment.CurrentManagedThreadId;
        var rootVisits = 0;
        for (var index = 0; index < widths.Length; index++)
        {
            var ordinal = index;
            var root = Directory.CreateDirectory(Path.Combine(_root, $"root-{index}")).FullName;
            if (widths[index] != 0)
                PopulateNarrowThenWideTree(root, widths[index]);
            expected[index] = ReferenceEntries(root);
            actual[index] = [];
            requests[index] = new WindowsDirectoryCollector.RootRequest(root, (path, directory) =>
            {
                Assert.Equal(caller, Environment.CurrentManagedThreadId);
                if (Path.GetDirectoryName(path) == root)
                    rootVisits++;
                else
                    Assert.Equal(4, rootVisits);
                actual[ordinal].Add((path, directory));
            });
        }

        WindowsDirectoryCollector.Collect(requests, TestContext.Current.CancellationToken);

        for (var index = 0; index < widths.Length; index++)
            Assert.True(expected[index].SequenceEqual(actual[index]),
                "Idle workers must survive empty roots and serve later narrow-to-wide root admissions.");
        AssertDirectoriesReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterRootVisitorFailureOrCancellation_DrainsEarlierRootAndStopsFurtherAdmission(bool cancel)
    {
        RequireWindows();
        PopulateNarrowThenWideTree(_root, 300);
        var later = Directory.CreateDirectory(Path.Combine(_root, "later")).FullName;
        File.WriteAllText(Path.Combine(later, "input.txt"), "included");
        var failure = new InvalidOperationException("The later root visitor rejected collection.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var laterVisits = 0;
        var unadmittedVisits = 0;
        WindowsDirectoryCollector.RootRequest[] roots =
        [
            new(_root, (_, _) => { }),
            new(later, (_, _) =>
            {
                laterVisits++;
                if (cancel)
                    cancellation.Cancel();
                else
                    throw failure;
            }),
            new(later, (_, _) => unadmittedVisits++)
        ];

        if (cancel)
        {
            var observed = Assert.ThrowsAny<OperationCanceledException>(() =>
                WindowsDirectoryCollector.Collect(roots, cancellation.Token));
            Assert.Equal(cancellation.Token, observed.CancellationToken);
        }
        else
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
                WindowsDirectoryCollector.Collect(roots, cancellation.Token)));

        Assert.Equal(1, laterVisits);
        Assert.Equal(0, unadmittedVisits);
        AssertDirectoriesReleased();
    }

    [Fact]
    public void MissingLaterRoot_PropagatesNativeErrorAndReleasesEarlierRoot()
    {
        RequireWindows();
        PopulateNarrowThenWideTree(_root, 300);
        var visits = 0;
        WindowsDirectoryCollector.RootRequest[] roots =
        [
            new(_root, (_, _) => { }),
            new(Path.Combine(_root, "missing"), (_, _) => visits++),
            new(_root, (_, _) => visits++)
        ];

        Assert.Throws<DirectoryNotFoundException>(() =>
            WindowsDirectoryCollector.Collect(roots, TestContext.Current.CancellationToken));

        Assert.Equal(0, visits);
        AssertDirectoriesReleased();
    }

    private void AssertDirectoriesReleased()
    {
        // An exclusive consumer must be able to reopen both the active directory and queued descendants after failure.
        var directories = Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Prepend(_root);
        foreach (var directory in directories)
        {
            using var handle = WindowsNative.CreateFile(directory, 1, 0, 0, 3, 0x02000000, 0);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The collector retained a directory handle after returning.");
        }
    }

    private static (string Path, bool Directory)[] ReferenceEntries(string root) =>
        new FileSystemEnumerable<(string Path, bool Directory)>(root,
            static (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
                BufferSize = 16 * 1024
            }).ToArray();

    private static string PopulateNarrowThenWideTree(string root, int width)
    {
        var frontier = root;
        for (var depth = 0; depth < 4; depth++)
            frontier = Directory.CreateDirectory(Path.Combine(frontier, $"level-{depth}")).FullName;
        for (var branch = 0; branch < width; branch++)
        {
            var child = Directory.CreateDirectory(Path.Combine(frontier, $"branch-{branch:000}")).FullName;
            var nested = Directory.CreateDirectory(Path.Combine(child, "nested")).FullName;
            File.WriteAllText(Path.Combine(nested, "input.txt"), "included");
        }
        return frontier;
    }

    private int PopulateTree()
    {
        var count = 0;
        for (var branch = 0; branch < 7; branch++)
        {
            var directory = Directory.CreateDirectory(Path.Combine(_root, $"branch-{branch:00}")).FullName;
            count++;
            // Force multiple 16 KiB native result buffers in every branch, with non-ASCII and long names.
            for (var file = 0; file < 96; file++)
            {
                File.WriteAllText(Path.Combine(directory, $"entry-{file:000}-λ-{new string('x', 80)}.txt"), "data");
                count++;
            }
            for (var depth = 0; depth < 3; depth++)
            {
                directory = Directory.CreateDirectory(Path.Combine(directory, $"level-{depth}")).FullName;
                File.WriteAllText(Path.Combine(directory, "same-length.txt"), "data");
                count += 2;
            }
        }
        return count;
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This collector regression requires the Windows filesystem enumeration implementation.");
    }
}
