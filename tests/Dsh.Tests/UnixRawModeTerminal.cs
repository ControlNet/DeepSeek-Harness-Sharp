using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Dsh.Boot;
using Dsh.Pty;

namespace Dsh.Tests;

/** A private kernel PTY with a native session owner, retained until restored termios has been inspected. */
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class UnixRawModeTerminal : IDisposable
{
    internal const string MainScreenMarker = "DSH-RAW-MAIN-SCREEN";

    [Flags]
    internal enum TermiosFields
    {
        None = 0,
        InputFlags = 1,
        OutputFlags = 2,
        ControlFlags = 4,
        LocalFlags = 8,
        AllControlCharacters = 16,
        InputSpeed = 32,
        OutputSpeed = 64,
        LinuxLineDiscipline = 128,
    }

    private readonly Process _process;
    private readonly BinaryReader _reader;
    private readonly BinaryWriter _writer;
    private readonly byte[] _buffer = new byte[65536];
    private bool _disposed;
    private int _frameEndPrefix;

    private UnixRawModeTerminal(Process process, string home)
    {
        _process = process;
        Home = home;
        _reader = new BinaryReader(process.StandardOutput.BaseStream, Encoding.UTF8, leaveOpen: true);
        _writer = new BinaryWriter(process.StandardInput.BaseStream, Encoding.UTF8);
    }

    public string Home { get; }
    public VtScreen Screen { get; } = new(140, 40);
    public int InitialMode { get; private set; }
    public int Mode { get; private set; }
    public bool HasRenderedFrame { get; private set; }

    public static UnixRawModeTerminal Start(bool copyRealConfiguration)
    {
        var executable = RealHostProcesses.Executable;
        var helper = Path.Combine(AppContext.BaseDirectory, "dsh_unix_raw_mode_terminal");
        Assert.True(File.Exists(helper), "The native Unix terminal fixture must be built with Dsh.Tests.");
        var home = TempTree.CreateSocketDirectory("r");
        UnixRawModeTerminal? terminal = null;
        try
        {
            File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (copyRealConfiguration)
            {
                var template = HarnessHome.Default().Root;
                Assert.True(File.Exists(Path.Combine(template, "settings.yaml")),
                    "The real TUI model smoke requires settings.yaml in the normal DSH_HOME template.");
                File.Copy(Path.Combine(template, "settings.yaml"), Path.Combine(home, "settings.yaml"));
                var profiles = Path.Combine(template, "profiles");
                if (Directory.Exists(profiles))
                    CopyDirectory(profiles, Path.Combine(home, "profiles"));
                // Retain real providers and safety settings, but never follow the template's storage redirects.
                var isolatedHome = new HarnessHome(home);
                var settings = HarnessSettings.Load(isolatedHome);
                settings.Storage = new StorageSettings { Root = home, ToolResults = settings.Storage?.ToolResults };
                settings.Save(isolatedHome);
            }

            var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            var startInfo = new ProcessStartInfo(helper, [executable, repository, home])
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                WorkingDirectory = repository,
            };
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The native Unix terminal fixture did not start.");
            terminal = new UnixRawModeTerminal(process, home);
            terminal.CheckResponse();
            terminal.InitialMode = terminal._reader.ReadInt32();
            return terminal;
        }
        catch
        {
            if (terminal is not null)
                terminal.Dispose();
            else
                TempTree.Delete(home);
            throw;
        }
    }

    public void Observe()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Send('O');
        CheckResponse();
        Mode = _reader.ReadInt32();
        var count = _reader.ReadInt32();
        var bytes = _buffer.AsSpan(0, count);
        _reader.BaseStream.ReadExactly(bytes);
        Screen.Feed(bytes);
        if (!HasRenderedFrame)
        {
            // DECSYNC commits the first real frame; entering the alternate screen happens before that draw.
            ReadOnlySpan<byte> frameEnd = "\u001b[?2026l"u8;
            foreach (var value in bytes)
            {
                _frameEndPrefix = value == frameEnd[_frameEndPrefix]
                    ? _frameEndPrefix + 1
                    : value == frameEnd[0] ? 1 : 0;
                if (_frameEndPrefix != frameEnd.Length) continue;
                HasRenderedFrame = true;
                break;
            }
        }
    }

    public void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _writer.Write((byte)'W');
        _writer.Write(bytes.Length);
        _writer.Write(bytes);
        _writer.Flush();
        CheckResponse();
    }

    public void Resize(int rows, int columns)
    {
        _writer.Write((byte)'R');
        _writer.Write(rows);
        _writer.Write(columns);
        _writer.Flush();
        CheckResponse();
        Assert.Equal(rows, _reader.ReadInt32());
        Assert.Equal(columns, _reader.ReadInt32());
        Screen.Resize(columns, rows);
    }

    public int? ExitCode()
    {
        Send('S');
        CheckResponse();
        var code = _reader.ReadInt32();
        return code < 0 ? null : code;
    }

    public (TermiosFields ChangedFields, bool KernelPendingRetype) CompareRestoredTermios()
    {
        Send('V');
        CheckResponse();
        return ((TermiosFields)_reader.ReadInt32(), _reader.ReadInt32() != 0);
    }

    public bool ContainsVisible(string marker, bool ignoreCase = false)
        => ContainsVisible(marker, 0, Screen.Height, ignoreCase);

    public bool ContainsVisible(string marker, int firstRow, int endRow, bool ignoreCase = false)
    {
        for (var y = firstRow; y < endRow; y++)
        {
            var row = Screen.Row(y);
            for (var start = 0; start < row.Length; start++)
            {
                var index = 0;
                for (var x = start; x < row.Length && index < marker.Length; x++)
                {
                    var character = row[x].Character;
                    if (character == '\0') continue; // CJK continuation cells do not add a character.
                    var expected = marker[index];
                    if (ignoreCase)
                    {
                        character = char.ToLowerInvariant(character);
                        expected = char.ToLowerInvariant(expected);
                    }
                    if (character != expected) break;
                    index++;
                }
                if (index == marker.Length) return true;
            }
        }
        return false;
    }

    private void Send(char command)
    {
        _writer.Write((byte)command);
        _writer.Flush();
    }

    private void CheckResponse()
    {
        var error = _reader.ReadInt32();
        Assert.True(error == 0, $"Native Unix terminal operation failed with errno {error}.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Closing the protocol input releases the native owner even when an assertion failed.
            _writer.Dispose();
            _process.WaitForExit();
        }
        finally
        {
            _reader.Dispose();
            _process.Dispose();
            TempTree.Delete(Home);
        }
    }
}
