using System.Collections;
using System.ComponentModel;
using System.IO.Enumeration;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
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

    [Fact]
    public void WideParentFanout_PreservesNativeOrderAndReleasesEveryDirectory()
    {
        RequireWindows();
        var (expectedCount, _) = PopulateParentFanout();
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
        var caller = Environment.CurrentManagedThreadId;

        WindowsDirectoryCollector.Collect(_root, (path, directory) =>
        {
            Assert.Equal(caller, Environment.CurrentManagedThreadId);
            actual.Add((path, directory));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(expectedCount, actual.Count);
        Assert.True(expected.SequenceEqual(actual),
            "Wide sibling parents and their children must preserve native entry and FIFO publication order.");
        AssertDirectoriesReleased();
    }

    [Fact]
    public void WideParentFanout_NativeChildOpenFailureReleasesEveryDirectory()
    {
        RequireWindows();
        var (_, leaf) = PopulateParentFanout();
        var actual = new List<string>();
        using (var exclusive = WindowsNative.CreateFile(leaf, 1, 0, 0, 3, 0x02000000, 0))
        {
            if (exclusive.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The exclusive leaf fixture could not open its directory.");

            var failure = Assert.Throws<IOException>(() => WindowsDirectoryCollector.Collect(_root,
                (path, _) => actual.Add(path), TestContext.Current.CancellationToken));

            Assert.Equal(unchecked((int)0x80070020), failure.HResult);
            Assert.DoesNotContain(Path.Combine(leaf, "input-λ.txt"), actual);
        }
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
        var queue = Activator.CreateInstance(queueType, CancellationToken.None)!;
        var gate = queueType.GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(queue)!;
        var run = queueType.GetMethod("Run")!.CreateDelegate<Action>(queue);
        var seal = queueType.GetMethod("Seal")!.CreateDelegate<Action>(queue);
        var fail = queueType.GetMethod("Fail")!.CreateDelegate<Action<Exception>>(queue);
        var demand = queueType.GetMethod("ThrowIfStopped")!.CreateDelegate<Action>(queue);
        Exception? cleanupFailure = null;
        var retire = queueType.GetMethod("RetireResources")!.CreateDelegate<Action>(queue);
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
            if (!caller.IsAlive && !worker.IsAlive)
                retire();
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterRootVisitorFailureOrCancellation_DrainsEarlierRootAndStopsFurtherAdmission(bool cancel)
    {
        RequireWindows();
        PopulateParentFanout();
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

    [Fact]
    public void RetainedParentAndFullCacheFallback_UseTheirActualAnchorsAfterParentReplacement()
    {
        const int parentCacheCapacity = 64;
        const int parentCount = parentCacheCapacity + 1;
        RequireWindows();
        var tree = CreateParentTree(parentCount);
        using (var component = new CollectorComponent(tree, TestContext.Current.CancellationToken))
        {
            var parents = new List<object>();
            for (var index = 0; index < parentCount; index++)
            {
                var parent = component.Take();
                component.Process(parent);
                parents.Add(parent);
            }
            var cached = CollectorComponent.PathOf(parents[^2]);
            var fallback = CollectorComponent.PathOf(parents[^1]);
            var movedCached = Path.Combine(_root, "moved-cached");
            var movedFallback = Path.Combine(_root, "moved-fallback");
            var grandchildPath = Directory.CreateDirectory(Path.Combine(cached, "child", "grandchild")).FullName;
            File.WriteAllText(Path.Combine(grandchildPath, "deep.txt"), "still pending when its ancestor retires");
            Directory.Move(cached, movedCached);
            Directory.Move(fallback, movedFallback);
            foreach (var path in new[] { cached, fallback })
            {
                var child = Directory.CreateDirectory(Path.Combine(path, "child")).FullName;
                File.WriteAllText(Path.Combine(child, "replacement.txt"), "replacement object");
            }

            var sawCached = false;
            var sawFallback = false;
            for (var index = 0; index < parentCount; index++)
            {
                var child = component.Take();
                component.Process(child);
                var path = CollectorComponent.PathOf(child);
                if (path == Path.Combine(cached, "child"))
                {
                    var entries = CollectorComponent.EntryPaths(child);
                    Assert.Contains(Path.Combine(path, "original.txt"), entries);
                    Assert.Contains(Path.Combine(path, "grandchild"), entries);
                    Assert.DoesNotContain(Path.Combine(path, "replacement.txt"), entries);
                    Assert.Equal(2, entries.Length);
                    sawCached = true;
                    // The retained ancestor is gone while its grandchild's native open is still pending.
                    AssertDirectoryReleased(movedCached);
                }
                if (path == Path.Combine(fallback, "child"))
                {
                    Assert.Equal(new[] { Path.Combine(path, "replacement.txt") }, CollectorComponent.EntryPaths(child));
                    sawFallback = true;
                }
            }
            Assert.True(sawCached);
            Assert.True(sawFallback);
            var grandchild = component.Take();
            Assert.Equal(Path.Combine(cached, "child", "grandchild"), CollectorComponent.PathOf(grandchild));
            component.Process(grandchild);
            Assert.Equal(new[] { Path.Combine(cached, "child", "grandchild", "deep.txt") }, CollectorComponent.EntryPaths(grandchild));
            component.Retire();
            component.AssertReclaimed();
        }
        AssertDirectoriesReleased();
    }

    [Theory]
    [InlineData("missing", 3)]
    [InlineData("file", 267)]
    [InlineData("locked", 32)]
    public void RetainedParent_ChildOpenFailureIsNotRetriedThroughReplacement(string change, int errorCode)
    {
        RequireWindows();
        var tree = CreateParentTree(1);
        using (var component = new CollectorComponent(tree, TestContext.Current.CancellationToken))
        {
            var parent = component.Take();
            component.Process(parent);
            var path = CollectorComponent.PathOf(parent);
            var moved = Path.Combine(_root, "moved-parent");
            Directory.Move(path, moved);
            Directory.CreateDirectory(Path.Combine(path, "child"));
            var oldChild = Path.Combine(moved, "child");
            SafeFileHandle? exclusive = null;
            try
            {
                if (change is "missing" or "file")
                {
                    Directory.Delete(oldChild, recursive: true);
                    if (change == "file")
                        File.WriteAllText(oldChild, "not a directory");
                }
                else
                {
                    exclusive = WindowsNative.CreateFile(oldChild, 1, 0, 0, 3, 0x02000000, 0);
                    if (exclusive.IsInvalid)
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "The exclusive cached child fixture could not open.");
                }
                var child = component.Take();

                var failure = Assert.ThrowsAny<IOException>(() => component.Process(child));

                Assert.Equal(unchecked((int)0x80070000) | errorCode, failure.HResult);
                Assert.Empty(CollectorComponent.EntryPaths(child));
                component.Fail(new InvalidOperationException("A later failure must not replace the native failure."));
                Assert.Same(failure, Record.Exception(component.Demand));
            }
            finally
            {
                exclusive?.Dispose();
            }
            component.Retire();
            component.AssertReclaimed();
        }
        AssertDirectoriesReleased();
    }

    [Fact]
    public void RetainedParent_CancellationBeforeNextOpenReclaimsUnopenedBorrowers()
    {
        RequireWindows();
        var tree = CreateParentTree(1);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using (var component = new CollectorComponent(tree, cancellation.Token))
        {
            component.Process(component.Take());
            var child = component.Take();
            cancellation.Cancel();

            var failure = Assert.ThrowsAny<OperationCanceledException>(() => component.Process(child));

            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Empty(CollectorComponent.EntryPaths(child));
            Assert.Same(failure, Record.Exception(component.Demand));
            InterruptAtGate(component.Gate, component.Retire);
            Assert.Same(failure, Record.Exception(component.Demand));
            component.AssertReclaimed();
        }
        AssertDirectoriesReleased();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RetainedParents_InterruptedPermitReturnOrCloseGateReclaimsResources(bool returnPermit, bool firstFailure)
    {
        RequireWindows();
        var tree = CreateParentTree(2);
        using (var component = new CollectorComponent(tree, TestContext.Current.CancellationToken))
        {
            component.Process(component.Take());
            component.Process(component.Take());
            if (returnPermit)
                component.Reserve(1);
            Exception? primary = null;
            if (firstFailure)
            {
                var child = component.Take();
                using var exclusive = WindowsNative.CreateFile(CollectorComponent.PathOf(child), 1, 0, 0, 3, 0x02000000, 0);
                if (exclusive.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "The exclusive first-error fixture could not open.");
                primary = Assert.Throws<IOException>(() => component.Process(child));
                Assert.Equal(unchecked((int)0x80070020), primary.HResult);
            }

            InterruptAtGate(component.Gate, returnPermit ? () => component.Return(1) : component.Retire);

            var observed = Record.Exception(component.Demand);
            if (firstFailure)
                Assert.Same(primary, observed);
            else
                Assert.IsType<ThreadInterruptedException>(observed);
            component.Retire();
            component.AssertReclaimed();
        }
        AssertDirectoriesReleased();
    }

    [Fact]
    public void SegmentedReservation_WaitsWithoutTakingTheLastSinglePermitAndFailureWakesIt()
    {
        const int maximumDescendantHandles = 511;
        const int segmentedOpenHandles = 2;
        const int occupiedPermits = maximumDescendantHandles - segmentedOpenHandles;
        RequireWindows();
        var tree = CreateParentTree(1);
        var deadline = TimeSpan.FromSeconds(5);
        using (var component = new CollectorComponent(tree, TestContext.Current.CancellationToken))
        {
            component.Process(component.Take());
            component.Reserve(occupiedPermits);
            var failure = new OperationCanceledException("The pending segmented open was stopped.");
            Exception? observed = null;
            var caller = new Thread(() =>
            {
                var acquired = false;
                try
                {
                    component.Reserve(segmentedOpenHandles);
                    acquired = true;
                }
                catch (Exception error)
                {
                    observed = error;
                }
                finally
                {
                    if (acquired)
                        component.Return(segmentedOpenHandles);
                }
            }) { IsBackground = true };
            try
            {
                caller.Start();
                Assert.True(SpinWait.SpinUntil(
                    () => (caller.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, deadline),
                    "A segmented reservation must wait when only one descendant permit remains.");
                Assert.Equal(1 + occupiedPermits, component.ReservedHandles);
                component.Fail(failure);
                Assert.True(caller.Join(deadline), "Failure must wake the pending atomic reservation.");
                Assert.Same(failure, observed);
            }
            finally
            {
                component.Fail(failure);
                if (caller.IsAlive)
                    Assert.True(caller.Join(deadline), "The owned reservation caller must exit.");
                component.Return(occupiedPermits);
            }
            component.Retire();
            component.AssertReclaimed();
        }
        AssertDirectoriesReleased();
    }

    [Fact]
    public void AclReadProof_ReopensDeclaredPathDescriptorRatherThanRetainedCollectorParent()
    {
        RequireWindows();
        var tree = CreateParentTree(1);
        // A synthetic, test-only inherited attribute marker distinguishes descriptors without granting AppContainer access.
        var marker = new SecurityIdentifier("S-1-5-21-0-0-0-271828");
        var markerRule = new FileSystemAccessRule(marker, FileSystemRights.ReadAttributes,
            InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
        using (var component = new CollectorComponent(tree, TestContext.Current.CancellationToken))
        {
            var parent = component.Take();
            component.Process(parent);
            var parentPath = CollectorComponent.PathOf(parent);
            var path = Path.Combine(parentPath, "child", "original.txt");
            var entryType = typeof(WindowsAclLease).GetNestedType("Entry", BindingFlags.NonPublic)!;
            var entry = Activator.CreateInstance(entryType, path, false)!;
            bool ReadProof() => (bool)CollectorComponent.Invoke(entryType, entry, "CanReuseReadGrant",
                marker, (int)markerRule.FileSystemRights, (int)markerRule.FileSystemRights,
                false, false, false, null)!;
            Assert.False(ReadProof());

            var moved = Path.Combine(_root, "descriptor-original");
            Directory.Move(parentPath, moved);
            var replacementParent = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var replacementAcl = replacementParent.GetAccessControl(AccessControlSections.Access);
            replacementAcl.AddAccessRule(markerRule);
            replacementParent.SetAccessControl(replacementAcl);
            File.WriteAllText(path, "replacement descriptor");
            var originalFile = new FileInfo(Path.Combine(moved, "child", "original.txt"));
            var currentFile = new FileInfo(path);
            var originalAcl = originalFile.GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            var currentAcl = currentFile.GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

            Assert.NotEqual(originalAcl, currentAcl);
            Assert.True(ReadProof());
            Assert.Equal(originalAcl, originalFile.GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Assert.Equal(currentAcl, currentFile.GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            component.Retire();
            component.AssertReclaimed();
        }
        AssertDirectoriesReleased();
    }

    private string CreateParentTree(int parentCount)
    {
        var tree = Directory.CreateDirectory(Path.Combine(_root, "component-tree")).FullName;
        for (var index = 0; index < parentCount; index++)
        {
            var child = Directory.CreateDirectory(Path.Combine(tree, $"parent-{index:000}", "child")).FullName;
            File.WriteAllText(Path.Combine(child, "original.txt"), "original object");
        }
        return tree;
    }

    private static void InterruptAtGate(object gate, Action operation)
    {
        var deadline = TimeSpan.FromSeconds(5);
        Exception? failure = null;
        var caller = new Thread(() =>
        {
            try
            {
                operation();
            }
            catch (Exception error)
            {
                failure = error;
            }
        }) { IsBackground = true };
        try
        {
            lock (gate)
            {
                caller.Start();
                Assert.True(SpinWait.SpinUntil(
                    () => (caller.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, deadline),
                    "Resource retirement must be waiting for its real accounting gate before interruption.");
                caller.Interrupt();
            }
            Assert.True(caller.Join(deadline), "Interrupted resource retirement must finish after the gate is released.");
            Assert.Null(failure);
        }
        finally
        {
            if (caller.IsAlive)
                Assert.True(caller.Join(deadline), "The owned resource-retirement thread must exit.");
        }
    }

    private sealed class CollectorComponent : IDisposable
    {
        private static readonly Type CollectorType = typeof(WindowsDirectoryCollector);
        private static readonly Type QueueType = CollectorType.GetNestedType("WorkQueue", BindingFlags.NonPublic)!;
        private readonly object _queue;
        private readonly SafeFileHandle _rootHandle;
        private nint _buffer;
        private bool _retired;

        public CollectorComponent(string path, CancellationToken cancellationToken)
        {
            _queue = Activator.CreateInstance(QueueType, cancellationToken)!;
            var rootType = CollectorType.GetNestedType("RootWork", BindingFlags.NonPublic)!;
            var root = Activator.CreateInstance(rootType, new WindowsDirectoryCollector.RootRequest(path, static (_, _) => { }))!;
            _rootHandle = WindowsNative.CreateFile(path, 1, 7, 0, 3, 0x02000000, 0);
            if (_rootHandle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                _rootHandle.Dispose();
                throw new Win32Exception(error, "The native collector component fixture could not open its root.");
            }
            try
            {
                rootType.GetProperty("Handle")!.SetValue(root, _rootHandle);
                _buffer = Marshal.AllocHGlobal(16 * 1024);
                var directory = rootType.GetProperty("Directory")!.GetValue(root)!;
                Invoke(CollectorType, null, "CollectDirectory", _rootHandle, directory, _buffer, cancellationToken, _queue);
                Invoke(CollectorType, null, "PublishDirectory", directory, rootType.GetProperty("Pending")!.GetValue(root),
                    rootType.GetProperty("Accept")!.GetValue(root), cancellationToken, _queue);
                Invoke(QueueType, _queue, "Admit", root);
                Invoke(QueueType, _queue, "Seal");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public object Gate => QueueType.GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_queue)!;
        public int ReservedHandles => (int)QueueType.GetField("_descendantHandles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_queue)!;
        public object Take() => Invoke(QueueType, _queue, "Take")!;
        public void Process(object directory) => Invoke(QueueType, _queue, "ProcessDirectory", directory, _buffer);
        public void Reserve(int count) => Invoke(QueueType, _queue, "ReserveHandles", count);
        public void Return(int count) => Invoke(QueueType, _queue, "ReleaseHandles", count);
        public void Fail(Exception error) => Invoke(QueueType, _queue, "Fail", error);
        public void Demand() => Invoke(QueueType, _queue, "ThrowIfStopped");

        public void Retire()
        {
            if (_retired)
                return;
            // This fixture drives actual production steps synchronously; there are no active worker borrowers.
            Invoke(QueueType, _queue, "RetireResources");
            _retired = true;
        }

        public void AssertReclaimed()
        {
            Assert.Equal(0, ReservedHandles);
            var parents = (Array)QueueType.GetField("_cachedParents", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_queue)!;
            Assert.All(parents.Cast<object?>(), static parent => Assert.Null(parent));
        }

        public static string PathOf(object directory) => (string)directory.GetType().GetProperty("Path")!.GetValue(directory)!;

        public static string[] EntryPaths(object directory)
        {
            var entries = (IEnumerable)directory.GetType().GetProperty("Entries")!.GetValue(directory)!;
            return entries.Cast<object>().Select(static entry => (string)entry.GetType().GetProperty("Path")!.GetValue(entry)!).ToArray();
        }

        public static object? Invoke(Type type, object? target, string name, params object?[] arguments) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)!
                .Invoke(target, BindingFlags.DoNotWrapExceptions, null, arguments, null);

        public void Dispose()
        {
            try
            {
                Retire();
            }
            finally
            {
                _rootHandle.Dispose();
                if (_buffer != 0)
                {
                    Marshal.FreeHGlobal(_buffer);
                    _buffer = 0;
                }
            }
        }
    }

    private void AssertDirectoriesReleased()
    {
        // An exclusive consumer must be able to reopen both the active directory and queued descendants after failure.
        var directories = Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Prepend(_root);
        foreach (var directory in directories)
            AssertDirectoryReleased(directory);
    }

    private static void AssertDirectoryReleased(string directory)
    {
        using var handle = WindowsNative.CreateFile(directory, 1, 0, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The collector retained a directory handle after returning.");
    }

    private (int Count, string Leaf) PopulateParentFanout()
    {
        var count = 0;
        var leaf = "";
        // More candidate parents than retained slots, with multiple children per parent.
        // Assertions deliberately do not require a particular scheduling-dependent cache hit.
        for (var parentIndex = 0; parentIndex < 96; parentIndex++)
        {
            var parent = Directory.CreateDirectory(Path.Combine(_root, $"parent-{parentIndex:000}-λ")).FullName;
            count++;
            for (var childIndex = 0; childIndex < 4; childIndex++)
            {
                leaf = Directory.CreateDirectory(Path.Combine(parent, $"child-{childIndex}-日本語")).FullName;
                File.WriteAllText(Path.Combine(leaf, "input-λ.txt"), "included");
                count += 2;
            }
        }
        return (count, leaf);
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
