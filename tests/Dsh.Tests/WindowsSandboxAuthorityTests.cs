using Dsh.Boot;
using Dsh.Plugins.Sandbox;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Dsh.Sandbox;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class WindowsSandboxAuthorityTests : IDisposable
{
    private readonly string _root = TempTree.CreateDirectory("windows-sandbox-authority");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    public void Dispose() => TempTree.Delete(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SandboxNative")]
    public async Task PathGrants_EnforceReadsAndWrites_AndAreRemovedAfterDisposal(bool protectedParent)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows AppContainer and native filesystem ACLs.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var input = Path.Combine(_root, "readable.txt");
        var secret = Path.Combine(_root, "not-granted.txt");
        var protectedDirectoryInfo = protectedParent
            ? Directory.CreateDirectory(Path.Combine(working, "config")) : new DirectoryInfo(working);
        var protectedDirectory = protectedDirectoryInfo.FullName;
        if (protectedParent)
        {
            var protectedAcl = protectedDirectoryInfo.GetAccessControl(AccessControlSections.Access);
            protectedAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            protectedDirectoryInfo.SetAccessControl(protectedAcl);
        }
        var denied = Path.Combine(protectedDirectory, "settings.yaml");
        const string readable = "readable\r\nsecond-readable-line\r\nlast-readable-line\r\n";
        File.WriteAllText(input, readable);
        File.WriteAllText(secret, "host-only");
        var settingsBytes = "unchanged"u8.ToArray();
        File.WriteAllBytes(denied, settingsBytes);
        var originalParentAcl = ReadDacl(_root);
        var originalProtectedDirectoryAcl = ReadDacl(protectedDirectory);
        var originalSecretAcl = ReadDacl(secret);
        var originalDirectoryAcl = ReadDacl(working);
        var originalInputAcl = ReadDacl(input);
        var originalDeniedAcl = ReadDacl(denied);
        string expectedDeniedAcl;
        var launch = PluginSandboxLaunch.Options(SandboxRuntimePaths.ForCurrentHost());
        await using (var consumer = await SandboxedProcess.StartAsync(launch with
        {
            WorkingDirectory = working,
            ReadOnlyPaths = [input],
            WritablePaths = [working],
            DeniedWritePaths = [denied],
            Arguments = [.. launch.Arguments.Take(launch.Arguments.Count - 1), SandboxFileGrantConsumer.Argument, input, secret, denied, protectedParent.ToString()],
        }, TestContext.Current.CancellationToken))
        {
            var output = new StreamReader(consumer.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = new StreamReader(consumer.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
            await consumer.StandardInput.WriteAsync("native-pipe\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await consumer.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            await consumer.StandardInput.DisposeAsync();
            var code = await consumer.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
            var stdout = await output;
            var stderr = await error;
            var diagnostic = System.Text.RegularExpressions.Regex.Replace(stderr, """(?:[A-Za-z]:[\\/]|/)[^\s"'<>]+""", "<path>");
            var consequences = "";
            if (code != 0)
            {
                var settingsUnchanged = File.Exists(denied) && (await File.ReadAllBytesAsync(denied, TestContext.Current.CancellationToken)).AsSpan().SequenceEqual(settingsBytes);
                var inputUnchanged = File.Exists(input) && await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken) == readable;
                consequences = $"allowedExists={File.Exists(Path.Combine(working, "allowed.txt"))}; renameBeforeExists={File.Exists(Path.Combine(working, "rename-before.txt"))}; renameAfterExists={File.Exists(Path.Combine(working, "rename-after.txt"))}; settingsUnchanged={settingsUnchanged}; inputUnchanged={inputUnchanged}";
            }
            var failure = $"The real Windows path consumer exited with {code} (0x{unchecked((uint)code):x8}); {consequences}; stdoutLength={stdout.Length}; stderr={diagnostic[..Math.Min(diagnostic.Length, 2048)]}";
            Assert.True(code == 0, failure);
            Assert.True(stdout.Contains("read-complete:readable/second-readable-line/last-readable-line", StringComparison.Ordinal), failure);
            Assert.Contains(SandboxFileGrantConsumer.Input, stdout);
            Assert.True(stderr.Contains("diagnostic", StringComparison.Ordinal), failure);
            Assert.True(!File.Exists(Path.Combine(working, "rename-before.txt")), failure);
            Assert.True(!File.Exists(Path.Combine(working, "rename-after.txt")), failure);
            expectedDeniedAcl = AddConcurrentReadAttributeGrant(denied, originalDeniedAcl);
        }
        Assert.Equal("allowed", File.ReadAllText(Path.Combine(working, "allowed.txt")).Trim());
        Assert.Equal(settingsBytes, File.ReadAllBytes(denied));
        Assert.Equal(readable, File.ReadAllText(input));
        Assert.Equal("host-only", File.ReadAllText(secret));
        AssertDaclRestored(originalParentAcl, _root);
        AssertDaclRestored(originalProtectedDirectoryAcl, protectedDirectory);
        AssertDaclRestored(originalSecretAcl, secret);
        AssertDaclRestored(originalDirectoryAcl, working);
        AssertDaclRestored(originalInputAcl, input);
        AssertDaclRestored(expectedDeniedAcl, denied);
        AssertInheritanceRestored(working, denied, protectedParent);
        File.WriteAllText(denied, "host-write-after-disposal");
        Assert.Equal("host-write-after-disposal", File.ReadAllText(denied));
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task ReadOnlyRoot_AllowsRuntimeCreatedDescendantsWithoutAllowingWrites()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows AppContainer and native filesystem access checks.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var readable = Directory.CreateDirectory(Path.Combine(_root, "readonly"));
        var acl = readable.GetAccessControl(AccessControlSections.Access);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-2"), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        readable.SetAccessControl(acl);
        var input = Path.Combine(readable.FullName, "input.txt");
        File.WriteAllText(input, "initial-read");
        var originalAcl = ReadDacl(readable.FullName);
        string lateFile;
        await using (var consumer = await SandboxedProcess.StartAsync(new SandboxProcessOptions
        {
            Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            WorkingDirectory = working,
            ReadOnlyPaths = [readable.FullName],
            Arguments = ["/D", "/Q", "/C",
                @"set /p gate=&type ..\readonly\input.txt&type ..\readonly\new\late.txt&(echo tampered>>..\readonly\input.txt) 2>nul&exit /b 0"],
        }, TestContext.Current.CancellationToken))
        {
            var output = new StreamReader(consumer.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = consumer.StandardError.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);
            // Runtime-created descendants exercise inherited authority after the real consumer starts and before its input is released.
            var lateDirectory = Directory.CreateDirectory(Path.Combine(readable.FullName, "new")).FullName;
            lateFile = Path.Combine(lateDirectory, "late.txt");
            File.WriteAllText(lateFile, "late-read");
            await consumer.StandardInput.WriteAsync("continue\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await consumer.StandardInput.DisposeAsync();
            Assert.Equal(0, await consumer.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(Deadline, TestContext.Current.CancellationToken));
            var stdout = await output;
            await error;
            Assert.Contains("initial-read", stdout);
            Assert.Contains("late-read", stdout);
            Assert.Equal("initial-read", File.ReadAllText(input));
        }
        AssertDaclRestored(originalAcl, readable.FullName);
        var hostAfter = Path.Combine(Path.GetDirectoryName(lateFile)!, "host-after.txt");
        File.WriteAllText(hostAfter, "host-after");
        Assert.Equal(ReadDacl(hostAfter), ReadDacl(lateFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SandboxNative")]
    public async Task ReadOnlyFile_WithLargeProtectedAcl_PreservesReadOnlyAuthorityAndRestoresAcl(bool existingReadGrant)
    {
        const int syntheticPrincipalCount = 400;
        const int firstPrincipalRid = 10000;
        const int largeDescriptorThreshold = 8192;
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows AppContainer and native filesystem ACLs.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var input = new FileInfo(Path.Combine(_root, "large-acl.txt"));
        File.WriteAllText(input.FullName, "large-descriptor-read");
        var acl = input.GetAccessControl(AccessControlSections.Access);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        // These distinct synthetic principals only enlarge this owned test file's descriptor.
        for (var index = 0; index < syntheticPrincipalCount; index++)
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier($"S-1-5-21-0-0-0-{firstPrincipalRid + index}"),
                FileSystemRights.ReadAttributes, AccessControlType.Allow));
        if (existingReadGrant)
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-2"),
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        input.SetAccessControl(acl);
        Assert.True(input.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm().Length > largeDescriptorThreshold);
        var originalAcl = ReadDacl(input.FullName);

        await using (var consumer = await SandboxedProcess.StartAsync(new SandboxProcessOptions
        {
            Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            WorkingDirectory = working,
            ReadOnlyPaths = [input.FullName],
            Arguments = ["/D", "/Q", "/V:ON", "/C",
                @"set dshLarge=&set /p dshLarge=<..\large-acl.txt&echo !dshLarge!&(echo tampered>>..\large-acl.txt) 2>nul&exit /b 0"],
        }, TestContext.Current.CancellationToken))
        {
            var output = new StreamReader(consumer.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = new StreamReader(consumer.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
            await consumer.StandardInput.DisposeAsync();
            Assert.Equal(0, await consumer.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(Deadline, TestContext.Current.CancellationToken));
            var stdout = await output;
            var diagnostic = System.Text.RegularExpressions.Regex.Replace(await error, """(?:[A-Za-z]:[\\/]|/)[^\s"'<>]+""", "<path>");
            Assert.True(stdout.Contains("large-descriptor-read", StringComparison.Ordinal), $"The real large-ACL file consumer returned no data; stderr={diagnostic}");
            Assert.Equal("large-descriptor-read", File.ReadAllText(input.FullName));
        }
        AssertDaclRestored(originalAcl, input.FullName);
        Assert.True(input.GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected);
        File.WriteAllText(input.FullName, "host-write-after-disposal");
        Assert.Equal("host-write-after-disposal", File.ReadAllText(input.FullName));
    }

    // Temporary API investigation only; remove after the dedicated Windows observation job.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "SandboxNative")]
    public void TemporaryDiagnostic_FileSecurityQueries_ReportComponentEquivalence(bool protectedFile, bool kernelFirst)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This diagnostic requires Windows native security queries.");
        var stage = 0;
        SecurityIdentifier? sid = null;
        object? parentEntry = null, childEntry = null;
        string? filePath = null;
        var parentOwned = false;
        var childOwned = false;
        var childCleanupAttempted = false;
        var parentCleanupAttempted = false;
        TemporaryDescriptorQuery94.Failure? operationFailure = null, childFailure = null, parentFailure = null;
        try
        {
            var parent = Directory.CreateDirectory(Path.Combine(_root, "descriptor-query"));
            var file = new FileInfo(Path.Combine(parent.FullName, "input.txt"));
            filePath = file.FullName;
            File.WriteAllText(filePath, "owned-descriptor-query");
            if (protectedFile)
            {
                var acl = file.GetAccessControl(AccessControlSections.Access);
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
                file.SetAccessControl(acl);
            }
            // This synthetic principal is only an ACL fixture, not an effective AppContainer token.
            sid = new SecurityIdentifier("S-1-5-21-0-0-0-424242");
            parentEntry = TemporaryDescriptorQuery94.NewEntry(parent.FullName);
            childEntry = TemporaryDescriptorQuery94.NewEntry(filePath);
            stage = 1;
            parentOwned = true;
            TemporaryDescriptorQuery94.Grant(parentEntry, sid, 0x1301bf, directory: true, restrict: false);
            TemporaryDescriptorQuery94.Observe(filePath, stage, protectedFile, kernelFirst, requireLowLabel: true);
            stage = 2;
            childOwned = true;
            TemporaryDescriptorQuery94.Grant(childEntry, sid, 0x1200a9, directory: false, restrict: true);
            TemporaryDescriptorQuery94.Observe(filePath, stage, protectedFile, kernelFirst, requireLowLabel: true);
        }
        catch (Exception error)
        {
            operationFailure = TemporaryDescriptorQuery94.NumericFailure(stage, error);
        }
        if (childOwned)
        {
            childOwned = false;
            childCleanupAttempted = true;
            try
            {
                TemporaryDescriptorQuery94.Remove(childEntry!, sid!);
            }
            catch (Exception error)
            {
                childFailure = TemporaryDescriptorQuery94.NumericFailure(3, error);
            }
        }
        if (operationFailure is null && childFailure is null)
        {
            try
            {
                TemporaryDescriptorQuery94.Observe(filePath!, 3, protectedFile, kernelFirst, requireLowLabel: true);
            }
            catch (Exception error)
            {
                operationFailure = TemporaryDescriptorQuery94.NumericFailure(3, error);
            }
        }
        if (parentOwned)
        {
            parentOwned = false;
            parentCleanupAttempted = true;
            try
            {
                TemporaryDescriptorQuery94.Remove(parentEntry!, sid!);
            }
            catch (Exception error)
            {
                parentFailure = TemporaryDescriptorQuery94.NumericFailure(4, error);
            }
        }
        if (operationFailure is null && childFailure is null && parentFailure is null)
        {
            try
            {
                TemporaryDescriptorQuery94.Observe(filePath!, 4, protectedFile, kernelFirst, requireLowLabel: false);
            }
            catch (Exception error)
            {
                operationFailure = TemporaryDescriptorQuery94.NumericFailure(4, error);
            }
        }
        var result = $"protected={protectedFile}; kernelFirst={kernelFirst}; operationFailed={operationFailure.HasValue}; operationPhase={operationFailure?.Phase ?? 0}; operationHResult={operationFailure?.HResult ?? 0}; operationNativeCode={operationFailure?.NativeCode ?? 0}; childCleanupAttempted={childCleanupAttempted}; childCleanupFailed={childFailure.HasValue}; childCleanupPhase={childFailure?.Phase ?? 0}; childCleanupHResult={childFailure?.HResult ?? 0}; childCleanupNativeCode={childFailure?.NativeCode ?? 0}; parentCleanupAttempted={parentCleanupAttempted}; parentCleanupFailed={parentFailure.HasValue}; parentCleanupPhase={parentFailure?.Phase ?? 0}; parentCleanupHResult={parentFailure?.HResult ?? 0}; parentCleanupNativeCode={parentFailure?.NativeCode ?? 0}; childOwned={childOwned}; parentOwned={parentOwned}";
        TestContext.Current.TestOutputHelper?.WriteLine(result);
        if (operationFailure.HasValue || childFailure.HasValue || parentFailure.HasValue)
            throw new InvalidOperationException(result);
    }

    // Temporary production-entry fixture and native materialization; no copied permission decisions.
    private static class TemporaryDescriptorQuery94
    {
        private static readonly Type EntryType = typeof(WindowsAclLease).GetNestedType("Entry", BindingFlags.NonPublic)!;
        private static readonly SecurityIdentifier LowLabel = new("S-1-16-4096");

        internal readonly record struct Failure(int Phase, int HResult, int NativeCode);

        internal static Failure NumericFailure(int phase, Exception error)
        {
            // Unwrap only reflection transport; each operation retains its own independent numeric failure.
            while (error is TargetInvocationException { InnerException: not null } invocation)
                error = invocation.InnerException;
            return new Failure(phase, error.HResult, error is Win32Exception native ? native.NativeErrorCode : 0);
        }

        internal static object NewEntry(string path) => Activator.CreateInstance(EntryType,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [path, false], null)!;

        internal static void Grant(object entry, SecurityIdentifier sid, int access, bool directory, bool restrict)
            => EntryType.GetMethod("Grant")!.Invoke(entry,
                [sid, access, access, directory, restrict, false, null, TestContext.Current.CancellationToken]);

        internal static void Remove(object entry, SecurityIdentifier sid)
            => EntryType.GetMethod("Remove")!.Invoke(entry, [sid]);

        internal static void Observe(string path, int phase, bool protectedFile, bool kernelFirst, bool requireLowLabel)
        {
            using var handle = WindowsNative.CreateFile(path, 0x20000, 7, 0, 3, 0x02000000, 0);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            // No file-security snapshot precedes the selected first query after the production mutation.
            var first = kernelFirst ? QueryKernel(handle) : QueryInfo(handle);
            var second = kernelFirst ? QueryInfo(handle) : QueryKernel(handle);
            // Repeating the first API detects changes during this query window, not changes inside its first call.
            var repeatedFirst = kernelFirst ? QueryKernel(handle) : QueryInfo(handle);
            var info = kernelFirst ? second : first;
            var kernel = kernelFirst ? first : second;
            var infoLow = HasLowLabel(info.Descriptor.SystemAcl);
            var kernelLow = HasLowLabel(kernel.Descriptor.SystemAcl);
            var totalKernelQueries = kernelFirst ? first.QueryCount + repeatedFirst.QueryCount : second.QueryCount;
            var infoQueries = kernelFirst ? 1 : 2;
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"phase={phase}; protected={protectedFile}; kernelFirst={kernelFirst}; infoLength={info.Length}; kernelLength={kernel.Length}; infoQueryCount={infoQueries}; kernelQueryCount={totalKernelQueries}; infoControl={info.Control}; kernelControl={kernel.Control}; infoDaclLength={info.Descriptor.DiscretionaryAcl?.BinaryLength ?? 0}; kernelDaclLength={kernel.Descriptor.DiscretionaryAcl?.BinaryLength ?? 0}; infoLabelLength={info.Descriptor.SystemAcl?.BinaryLength ?? 0}; kernelLabelLength={kernel.Descriptor.SystemAcl?.BinaryLength ?? 0}; infoInheritedAceCount={info.Descriptor.DiscretionaryAcl?.Cast<GenericAce>().Count(static ace => (ace.AceFlags & AceFlags.Inherited) != 0) ?? 0}; kernelInheritedAceCount={kernel.Descriptor.DiscretionaryAcl?.Cast<GenericAce>().Count(static ace => (ace.AceFlags & AceFlags.Inherited) != 0) ?? 0}; daclEqual={AclEqual(info.Descriptor.DiscretionaryAcl, kernel.Descriptor.DiscretionaryAcl)}; controlEqual={info.Control == kernel.Control}; ownerEqual={Equals(info.Descriptor.Owner, kernel.Descriptor.Owner)}; groupEqual={Equals(info.Descriptor.Group, kernel.Descriptor.Group)}; labelEqual={AclEqual(info.Descriptor.SystemAcl, kernel.Descriptor.SystemAcl)}; infoLowLabel={infoLow}; kernelLowLabel={kernelLow}; headerMatchesRaw={kernel.HeaderMatches}; firstQueryStable={Same(first, repeatedFirst)}");
            if (requireLowLabel)
                Assert.True(infoLow && kernelLow, "Temporary diagnostic did not reach the required nonempty Low label.");
        }

        private sealed record Snapshot(RawSecurityDescriptor Descriptor, uint Length, ushort Control, int QueryCount, bool HeaderMatches);

        private static Snapshot QueryInfo(SafeFileHandle handle)
        {
            var error = WindowsNative.GetSecurityInfo(handle, 1, 0x17, out var owner, out var group,
                out var dacl, out var label, out var pointer);
            if (error != 0)
                throw new Win32Exception((int)error);
            try
            {
                if (!WindowsNative.GetSecurityDescriptorControl(pointer, out var control, out _))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                var descriptor = new RawSecurityDescriptor((ControlFlags)control,
                    owner == 0 ? null : new SecurityIdentifier(owner), group == 0 ? null : new SecurityIdentifier(group),
                    ReadAcl(label), ReadAcl(dacl));
                return new Snapshot(descriptor, WindowsNative.GetSecurityDescriptorLength(pointer), control, 1, true);
            }
            finally { WindowsNative.LocalFree(pointer); }
        }

        private static unsafe Snapshot QueryKernel(SafeFileHandle handle)
        {
            Span<byte> buffer = stackalloc byte[1024];
            var queryCount = 0;
            while (true)
            {
                uint needed;
                int error;
                fixed (byte* pointer = buffer)
                {
                    queryCount++;
                    if (GetKernelObjectSecurity(handle, 0x17, (nint)pointer, (uint)buffer.Length, out needed))
                    {
                        var bytes = new byte[checked((int)needed)];
                        Marshal.Copy((nint)pointer, bytes, 0, bytes.Length);
                        var descriptor = new RawSecurityDescriptor(bytes, 0);
                        var header = (SecurityDescriptorRelative*)pointer;
                        var owner = header->Owner == 0 ? null : new SecurityIdentifier((nint)(pointer + header->Owner));
                        var group = header->Group == 0 ? null : new SecurityIdentifier((nint)(pointer + header->Group));
                        var dacl = ReadAcl(header->Dacl == 0 ? 0 : (nint)(pointer + header->Dacl));
                        var label = ReadAcl(header->Sacl == 0 ? 0 : (nint)(pointer + header->Sacl));
                        var matches = header->Control == (ushort)descriptor.ControlFlags
                            && Equals(owner, descriptor.Owner) && Equals(group, descriptor.Group)
                            && AclEqual(dacl, descriptor.DiscretionaryAcl) && AclEqual(label, descriptor.SystemAcl);
                        return new Snapshot(descriptor, needed, header->Control, queryCount, matches);
                    }
                    error = Marshal.GetLastPInvokeError();
                }
                if (error != 122 || needed <= (uint)buffer.Length)
                    throw new Win32Exception(error);
                buffer = new byte[checked((int)needed)];
            }
        }

        private static bool Same(Snapshot left, Snapshot right) => left.Control == right.Control
            && Equals(left.Descriptor.Owner, right.Descriptor.Owner) && Equals(left.Descriptor.Group, right.Descriptor.Group)
            && AclEqual(left.Descriptor.DiscretionaryAcl, right.Descriptor.DiscretionaryAcl)
            && AclEqual(left.Descriptor.SystemAcl, right.Descriptor.SystemAcl);

        private static bool HasLowLabel(RawAcl? acl)
        {
            if (acl is null)
                return false;
            foreach (GenericAce ace in acl)
            {
                if ((byte)ace.AceType != 0x11)
                    continue;
                var bytes = new byte[ace.BinaryLength];
                ace.GetBinaryForm(bytes, 0);
                // SYSTEM_MANDATORY_LABEL_ACE stores its integrity SID after the header and mask.
                if (new SecurityIdentifier(bytes, 8).Equals(LowLabel))
                    return true;
            }
            return false;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint information, nint descriptor, uint length, out uint needed);

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityDescriptorRelative
        {
            public byte Revision, ResourceManagerControl;
            public ushort Control;
            public uint Owner, Group, Sacl, Dacl;
        }

        private static RawAcl? ReadAcl(nint pointer)
        {
            if (pointer == 0)
                return null;
            var bytes = new byte[(ushort)Marshal.ReadInt16(pointer, 2)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return new RawAcl(bytes, 0);
        }

        private static bool AclEqual(RawAcl? left, RawAcl? right)
        {
            if (left is null || right is null)
                return left is null && right is null;
            var leftBytes = new byte[left.BinaryLength];
            var rightBytes = new byte[right.BinaryLength];
            left.GetBinaryForm(leftBytes, 0);
            right.GetBinaryForm(rightBytes, 0);
            return leftBytes.AsSpan().SequenceEqual(rightBytes);
        }
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public void ReadableRootGroups_MergeIndependentTreesBeforeOverlappingWriteOverridesAreReused()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This collection regression requires Windows native directory reads.");
        var parent = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        var peer = Directory.CreateDirectory(Path.Combine(_root, "b")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(parent, "nested")).FullName;
        var input = Path.Combine(nested, "input.txt");
        var peerInput = Path.Combine(peer, "input.txt");
        var writable = Path.Combine(_root, "writable.txt");
        File.WriteAllText(input, "readable");
        File.WriteAllText(peerInput, "peer-readable");
        File.WriteAllText(writable, "writable");
        var paths = new Dictionary<string, (bool Write, bool Directory)>(StringComparer.OrdinalIgnoreCase)
        {
            [nested] = (true, true),
            [input] = (true, false),
            [writable] = (true, false)
        };
        SandboxPath[] roots = [new(parent, parent, true), new(peer, peer, true), new(nested, nested, true)];

        WindowsAclLease.AddReadableTrees(paths, roots, Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            TestContext.Current.CancellationToken);

        Assert.Equal(6, paths.Count);
        Assert.Equal((false, true), paths[parent]);
        Assert.Equal((false, true), paths[peer]);
        Assert.Equal((false, true), paths[nested]);
        Assert.Equal((false, false), paths[input]);
        Assert.Equal((false, false), paths[peerInput]);
        Assert.Equal((true, false), paths[writable]);
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public void ReadableRootReuse_RequiresAnActuallyPublishedMatchingAccessAndDirectoryKind()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This collection regression requires Windows native directory reads.");
        var root = Directory.CreateDirectory(Path.Combine(_root, "readable")).FullName;
        var input = Path.Combine(root, "input.txt");
        File.WriteAllText(input, "readable");
        var paths = new Dictionary<string, (bool Write, bool Directory)>(StringComparer.OrdinalIgnoreCase);
        SandboxPath[] roots = [new(root, root, true)];
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        WindowsAclLease.AddReadableTrees(paths, roots, windows, TestContext.Current.CancellationToken);
        Directory.Delete(root, recursive: true);

        WindowsAclLease.AddReadableTrees(paths, roots, windows, TestContext.Current.CancellationToken);

        Assert.Equal(2, paths.Count);
        Assert.Equal((false, false), paths[input]);
        paths[root] = (false, false);
        Assert.Throws<DirectoryNotFoundException>(() =>
            WindowsAclLease.AddReadableTrees(paths, roots, windows, TestContext.Current.CancellationToken));
        paths[root] = (true, true);
        Assert.Throws<DirectoryNotFoundException>(() =>
            WindowsAclLease.AddReadableTrees(paths, roots, windows, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task ProtectedSettings_WithAmbientNetworkCapabilityWrite_RefusesLaunchBeforeExecution()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows AppContainer and native filesystem ACLs.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var settings = Path.Combine(working, "settings.yaml");
        var settingsBytes = "unchanged"u8.ToArray();
        await File.WriteAllBytesAsync(settings, settingsBytes, TestContext.Current.CancellationToken);
        var file = new FileInfo(settings);
        var acl = file.GetAccessControl(AccessControlSections.Access);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-3-1"), FileSystemRights.WriteData, AccessControlType.Allow));
        file.SetAccessControl(acl);
        var originalDirectoryAcl = ReadDacl(working);
        var originalSettingsAcl = ReadDacl(settings);
        var resumedMarker = Path.Combine(working, "resumed.txt");

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await using var consumer = await SandboxedProcess.StartAsync(new SandboxProcessOptions
            {
                Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                WorkingDirectory = working,
                WritablePaths = [working],
                DeniedWritePaths = [settings],
                AllowNetwork = true,
                Arguments = ["/D", "/Q", "/C", "echo resumed>resumed.txt"],
            }, TestContext.Current.CancellationToken);
        });

        Assert.Equal(settingsBytes, await File.ReadAllBytesAsync(settings, TestContext.Current.CancellationToken));
        AssertDaclRestored(originalDirectoryAcl, working);
        AssertDaclRestored(originalSettingsAcl, settings);
        AssertInheritanceRestored(working, settings, protectedChild: false);
        Assert.False(File.Exists(resumedMarker));
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task ReadOnlyDirectory_WithAmbientInheritOnlyWriteGrant_RefusesLaunchAndPreservesAcl()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows AppContainer and native filesystem ACLs.");
        var directory = Directory.CreateDirectory(Path.Combine(_root, "readonly"));
        var acl = directory.GetAccessControl(AccessControlSections.Access);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-1"), FileSystemRights.WriteData,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        directory.SetAccessControl(acl);
        var originalAcl = ReadDacl(directory.FullName);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await using var consumer = await SandboxedProcess.StartAsync(new SandboxProcessOptions
            {
                Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                WorkingDirectory = directory.FullName,
                ReadOnlyPaths = [directory.FullName],
                Arguments = ["/D", "/Q", "/C", "exit", "0"],
            }, TestContext.Current.CancellationToken);
        });

        Assert.Equal(originalAcl, ReadDacl(directory.FullName));
    }

    private static string AddConcurrentReadAttributeGrant(string path, string original)
    {
        // This synthetic SID is a test-only principal, not a real account.
        var principal = new SecurityIdentifier("S-1-5-21-0-0-0-271828");
        var rule = new FileSystemAccessRule(principal, FileSystemRights.ReadAttributes, AccessControlType.Allow);
        var expected = new FileSecurity();
        expected.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
        expected.AddAccessRule(rule);
        var file = new FileInfo(path);
        var current = file.GetAccessControl(AccessControlSections.Access);
        current.AddAccessRule(rule);
        file.SetAccessControl(current);
        return expected.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    }


    private static void AssertDaclRestored(string expected, string path)
    {
        var before = new RawSecurityDescriptor(expected);
        var after = new RawSecurityDescriptor(ReadDacl(path));
        // SetSecurityInfo converts legacy ACLs to automatic inheritance without changing their ACE semantics.
        // Permit only that one-way metadata transition; retain every ACE, its order and flags, and all other control bits.
        // https://learn.microsoft.com/en-us/windows/win32/secauthz/automatic-propagation-of-inheritable-aces
        if ((before.ControlFlags & ControlFlags.DiscretionaryAclAutoInherited) == 0)
            after.SetFlags(after.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited);
        Assert.Equal(before.ControlFlags, after.ControlFlags);
        Assert.Equal(before.GetSddlForm(AccessControlSections.Access), after.GetSddlForm(AccessControlSections.Access));
    }

    private static void AssertInheritanceRestored(string parent, string child, bool protectedChild)
    {
        var marker = new SecurityIdentifier("S-1-5-21-0-0-0-314159");
        var rule = new FileSystemAccessRule(marker, FileSystemRights.ReadAttributes,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
        var directory = new DirectoryInfo(parent);
        var originalParent = ReadDacl(parent);
        var originalChild = ReadDacl(child);
        var acl = directory.GetAccessControl(AccessControlSections.Access);
        var created = Path.Combine(parent, "host-created-after-disposal.txt");
        acl.AddAccessRule(rule);
        directory.SetAccessControl(acl);
        try
        {
            var inherited = new FileInfo(child).GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Any(access => marker.Equals(access.IdentityReference)
                    && access.IsInherited && access.FileSystemRights == rule.FileSystemRights);
            Assert.Equal(!protectedChild, inherited);
            File.WriteAllText(created, "host-created");
            Assert.Contains(new FileInfo(created).GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>(), access => marker.Equals(access.IdentityReference)
                    && access.IsInherited && access.FileSystemRights == rule.FileSystemRights);
        }
        finally
        {
            acl.RemoveAccessRuleSpecific(rule);
            directory.SetAccessControl(acl);
            File.Delete(created);
        }
        AssertDaclRestored(originalParent, parent);
        AssertDaclRestored(originalChild, child);
    }

    private static string ReadDacl(string path) => Directory.Exists(path)
        ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access)
        : new FileInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
}
