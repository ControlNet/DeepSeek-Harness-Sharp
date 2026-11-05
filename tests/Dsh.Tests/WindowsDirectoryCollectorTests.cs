using System.ComponentModel;
using System.IO.Enumeration;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dsh.Sandbox;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Tests;

[SupportedOSPlatform("windows")]
[Trait("Category", "SandboxNative")]
public sealed class WindowsDirectoryCollectorTests : IDisposable
{
    private readonly string _root = TempTree.CreateDirectory("windows-directory-collector");

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void SuccessfulQueryWithoutCompleteRecords_PropagatesFailureInsteadOfPublishingPartialTree()
    {
        RequireWindows();

        var failure = Assert.Throws<IOException>(() =>
            WindowsDirectoryCollector.HasCompleteDirectoryBatch(0, 0, _root));

        Assert.Equal(unchecked((int)0x8007007a), failure.HResult);
    }

    [Theory]
    [InlineData(7, 96)]
    [InlineData(300, 2)]
    public void CompleteTree_PreservesRecursiveEnumerationAndStableGrantOrder(int branches, int filesPerBranch)
    {
        RequireWindows();
        var expectedCount = PopulateTree(branches, filesPerBranch);
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
        AssertDirectoriesReleased();
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"\\?\")]
    [InlineData(@"\\.\")]
    public void DosAndDeviceRoots_PreserveUnicodeDescendantsAndReleaseHandles(string prefix)
    {
        RequireWindows();
        var root = $"{prefix}{_root}";
        var child = Path.Join(root, "λ-child");
        var nested = Path.Join(child, "nested");
        Directory.CreateDirectory(nested);
        var file = Path.Join(nested, "input.txt");
        File.WriteAllText(file, "included");
        var actual = new List<(string Path, bool Directory)>();

        WindowsDirectoryCollector.Collect(root, (path, directory) => actual.Add((path, directory)),
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { (child, true), (nested, true), (file, false) }, actual);
        AssertDirectoriesReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootRenamedBeforeDescendantOpen_ReResolvesDiscoveredAbsolutePath(bool replaceRoot)
    {
        RequireWindows();
        var root = Directory.CreateDirectory(Path.Join(_root, "tree")).FullName;
        var child = Directory.CreateDirectory(Path.Join(root, "child")).FullName;
        File.WriteAllText(Path.Join(child, "old.txt"), "original tree");
        var replacementFile = Path.Join(child, "replacement.txt");
        var actual = new List<(string Path, bool Directory)>();

        void Accept(string path, bool directory)
        {
            actual.Add((path, directory));
            if (path != child)
                return;
            Directory.Move(root, Path.Join(_root, "moved"));
            if (replaceRoot)
            {
                Directory.CreateDirectory(child);
                File.WriteAllText(replacementFile, "replacement tree");
            }
        }

        if (replaceRoot)
        {
            WindowsDirectoryCollector.Collect(root, Accept, TestContext.Current.CancellationToken);
            Assert.Equal(new[] { (child, true), (replacementFile, false) }, actual);
        }
        else
        {
            Assert.Throws<DirectoryNotFoundException>(() =>
                WindowsDirectoryCollector.Collect(root, Accept, TestContext.Current.CancellationToken));
            Assert.Equal(new[] { (child, true) }, actual);
        }
        AssertDirectoriesReleased();
    }

    [Fact]
    public void NativeCountedNameBoundaryAndOverlongRelativePath_CollectAllRealDirectories()
    {
        RequireWindows();
        using (var tree = new NativeDeepTree(_root))
        {
            // UNICODE_STRING counts UTF-16 bytes without requiring a trailing NUL.
            const int maximumNameCharacters = ushort.MaxValue / sizeof(char);
            const int maximumComponentCharacters = 240;
            const int dosNtPrefixCharacters = 4;
            var parent = 0;
            var branchPathLength = maximumNameCharacters - dosNtPrefixCharacters - maximumComponentCharacters - 1;
            while (tree.Paths[parent].Length < branchPathLength)
            {
                var remaining = branchPathLength - tree.Paths[parent].Length - 1;
                var length = Math.Min(maximumComponentCharacters, remaining);
                if (remaining - length == 1)
                    length--;
                parent = tree.Add(parent, new string('λ', length));
            }
            tree.Add(parent, new string('a', maximumComponentCharacters - 1));
            tree.Add(parent, new string('b', maximumComponentCharacters));
            var deepest = tree.Add(parent, new string('c', maximumComponentCharacters + 1));
            while (tree.Paths[deepest].Length - _root.Length - 1 <= maximumNameCharacters)
                deepest = tree.Add(deepest, new string('d', maximumComponentCharacters));
            tree.Add(deepest, "last");
            var actual = new List<string>();

            WindowsDirectoryCollector.Collect(_root, (path, directory) =>
            {
                Assert.True(directory);
                actual.Add(path);
            }, TestContext.Current.CancellationToken);

            Assert.Equal(tree.Paths.Count - 1, actual.Count);
            Assert.True(new HashSet<string>(tree.Paths.Skip(1), StringComparer.OrdinalIgnoreCase).SetEquals(actual),
                "Counted-name boundaries and segmented relative opens must not truncate the collected tree.");
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
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
        PopulateTree();
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

    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    public void ExclusivelyOpenedNestedDirectory_IsReportedAndReleasesAncestors(int siblingBranches)
    {
        RequireWindows();
        if (siblingBranches != 0)
            PopulateTree(siblingBranches, 1);
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
    [InlineData(31, false)]
    [InlineData(32, false)]
    [InlineData(33, false)]
    [InlineData(65, false)]
    [InlineData(65, true)]
    public void DeepTree_PreservesReferenceOrderAndReleasesParentHandles(int depth, bool longNames)
    {
        RequireWindows();
        var directory = _root;
        var name = longNames ? new string('λ', 96) : "d";
        for (var index = 0; index < depth; index++)
        {
            directory = Directory.CreateDirectory(Path.Combine(directory, name)).FullName;
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
    [InlineData(32, false)]
    [InlineData(33, false)]
    [InlineData(65, false)]
    [InlineData(65, true)]
    public void ExclusivelyOpenedDeepDirectory_IsReportedAndReleasesParentHandles(int depth, bool longNames)
    {
        RequireWindows();
        var directory = _root;
        var name = longNames ? new string('λ', 96) : "d";
        for (var index = 0; index < depth; index++)
            directory = Directory.CreateDirectory(Path.Combine(directory, name)).FullName;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedSealingOrHandleReturn_RetiresWaitingWorkersBeforeFailureIsPropagated(bool returnHandles)
    {
        RequireWindows();
        Assert.IsType<ThreadInterruptedException>(RunInterruptedCleanup(null, returnHandles));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedFailureCleanupOrHandleReturn_RetiresWorkersAndPreservesThePrimaryFailure(bool returnHandles)
    {
        RequireWindows();
        var failure = new IOException("The native operation failed before cleanup.");
        Assert.Same(failure, RunInterruptedCleanup(failure, returnHandles));
    }

    private static Exception? RunInterruptedCleanup(Exception? primaryFailure, bool returnHandles)
    {
        const int threadDeadlineSeconds = 5;
        var deadline = TimeSpan.FromSeconds(threadDeadlineSeconds);
        var queueType = typeof(WindowsDirectoryCollector).GetNestedType("WorkQueue", BindingFlags.NonPublic)!;
        var queue = Activator.CreateInstance(queueType, CancellationToken.None)!;
        var gate = queueType.GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(queue)!;
        var run = queueType.GetMethod("Run")!.CreateDelegate<Action>(queue);
        var seal = queueType.GetMethod("Seal")!.CreateDelegate<Action>(queue);
        var fail = queueType.GetMethod("Fail")!.CreateDelegate<Action<Exception>>(queue);
        var demand = queueType.GetMethod("ThrowIfStopped")!.CreateDelegate<Action>(queue);
        var returnReservation = queueType.GetMethod("ReturnHandles", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Action<int>>(queue);
        if (returnHandles)
        {
            queueType.GetMethod("RentHandles", BindingFlags.NonPublic | BindingFlags.Instance)!
                .CreateDelegate<Action<int>>(queue)(2);
            if (primaryFailure is not null)
                fail(primaryFailure);
        }
        Exception? cleanupFailure = null;
        var worker = new Thread(run.Invoke) { IsBackground = true };
        var caller = new Thread(() =>
        {
            try
            {
                if (returnHandles)
                    returnReservation(2);
                else if (primaryFailure is null)
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

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(300)]
    public void IndependentRoots_PreserveEachReferenceOrderAndCallerPublicationAcrossAdmissionSlots(int branches)
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
            for (var branch = 0; branch < branches; branch++)
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
                    Assert.Equal(roots.Length * branches, rootVisits);
                actual[ordinal].Add((path, directory));
            });
        }

        WindowsDirectoryCollector.Collect(requests, TestContext.Current.CancellationToken);

        for (var index = 0; index < roots.Length; index++)
            Assert.True(expected[index].SequenceEqual(actual[index]),
                "Every admitted root must retain its complete native entry and FIFO directory order.");
        AssertDirectoriesReleased();
    }

    [Theory]
    [InlineData(false, 7, 96)]
    [InlineData(true, 7, 96)]
    [InlineData(false, 300, 1)]
    [InlineData(true, 300, 1)]
    public void LaterRootVisitorFailureOrCancellation_DrainsEarlierRootAndStopsFurtherAdmission(
        bool cancel, int branches, int filesPerBranch)
    {
        RequireWindows();
        PopulateTree(branches, filesPerBranch);
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
        PopulateTree();
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

    // Fixture-only relative creation makes genuine paths beyond Win32's full-path limit.
    private sealed unsafe class NativeDeepTree : IDisposable
    {
        private const uint DirectoryAccess = 0x00110001; // DELETE | SYNCHRONIZE | FILE_LIST_DIRECTORY.
        private const uint ShareAll = 7;
        private const uint DirectoryOpenOptions = 0x4021; // Backup intent, synchronous non-alert I/O, directory.
        private readonly List<SafeFileHandle> _handles = [];
        public List<string> Paths { get; } = [];

        public NativeDeepTree(string root)
        {
            var handle = WindowsNative.CreateFile(root, DirectoryAccess, ShareAll, 0, 3, 0x02000000, 0);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(error, "Unable to open the deep-tree fixture root.");
            }
            _handles.Add(handle);
            Paths.Add(root);
        }

        public int Add(int parent, string component)
        {
            fixed (char* text = component)
            {
                var name = new NativeName
                {
                    Length = checked((ushort)(component.Length * sizeof(char))),
                    MaximumLength = checked((ushort)(component.Length * sizeof(char))),
                    Buffer = text
                };
                var attributes = new NativeAttributes
                {
                    Length = (uint)sizeof(NativeAttributes),
                    RootDirectory = _handles[parent].DangerousGetHandle(),
                    ObjectName = &name,
                    Attributes = 0x40
                };
                var status = NtCreateFile(out var handle, DirectoryAccess, ref attributes, out _, 0,
                    (uint)FileAttributes.Directory, ShareAll, 2, DirectoryOpenOptions, 0, 0);
                if (status < 0)
                {
                    handle.Dispose();
                    throw new Win32Exception((int)RtlNtStatusToDosError(status), "Unable to create a relative deep-tree fixture directory.");
                }
                _handles.Add(handle);
                Paths.Add(Path.Join(Paths[parent], component));
                return Paths.Count - 1;
            }
        }

        public void Dispose()
        {
            List<Exception>? failures = null;
            for (var index = _handles.Count - 1; index >= 0; index--)
            {
                using var handle = _handles[index];
                if (index == 0)
                    continue;
                byte delete = 1;
                if (!SetFileInformationByHandle(handle, 4, ref delete, sizeof(byte)))
                    (failures ??= []).Add(new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to delete a deep-tree fixture directory."));
            }
            if (failures is not null)
                throw new AggregateException("Deep-tree fixture cleanup failed.", failures);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeName
        {
            public ushort Length, MaximumLength;
            public char* Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeAttributes
        {
            public uint Length;
            public nint RootDirectory;
            public NativeName* ObjectName;
            public uint Attributes;
            public nint SecurityDescriptor, SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeIoStatus
        {
            public nint Status;
            public nuint Information;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref NativeAttributes attributes,
            out NativeIoStatus statusBlock, nint allocationSize, uint fileAttributes, uint share, uint disposition,
            uint options, nint eaBuffer, uint eaLength);

        [DllImport("ntdll.dll")]
        private static extern uint RtlNtStatusToDosError(int status);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, uint informationClass, ref byte information, uint size);
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

    private int PopulateTree(int branches = 7, int filesPerBranch = 96)
    {
        var count = 0;
        for (var branch = 0; branch < branches; branch++)
        {
            var directory = Directory.CreateDirectory(Path.Combine(_root, $"branch-{branch:00}")).FullName;
            count++;
            // Exercise Unicode and long names, with larger inputs spanning multiple native result buffers.
            for (var file = 0; file < filesPerBranch; file++)
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
