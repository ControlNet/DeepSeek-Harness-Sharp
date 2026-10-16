using System.Diagnostics;
using System.Runtime.Versioning;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Tui;

namespace Dsh.Tests;

/** Real CLI consumers of the kernel terminal, not the daemon/proxy snapshot harness. */
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[Collection(SerialProcessCollection.Name)]
public sealed class TuiRawModeEndToEndTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void RealCli_RawInputSurvivesBackgroundRefreshAndKernelResize_ThenRestoresTerminal(int run)
    {
        RequireUnix();
        using var terminal = UnixRawModeTerminal.Start(copyRealConfiguration: false);
        var scenario = new Scenario(terminal);
        scenario.WaitForRawInput();
        foreach (var probe in new[] { "zqx", "vkj", "ypw" })
            scenario.ExerciseInputAcrossRefreshAndResize(probe);
        scenario.ExitAndAssertRestored(output, $"run {run}");
    }

    [Fact]
    [Trait("Category", "LlmSmoke")]
    public void RealCli_RealAssistantTurnRenders_ThenRestoresTerminal()
    {
        RequireUnix();
        using var terminal = UnixRawModeTerminal.Start(copyRealConfiguration: true);
        var scenario = new Scenario(terminal);
        scenario.WaitForRawInput();
        // The answer is deliberately absent from the prompt: input echo cannot satisfy the assertion.
        terminal.Write("Spell out in English the number equal to six times seven. Reply only with the answer, without digits or tools.\r");
        scenario.WaitFor(() => terminal.ContainsVisible("forty-two", ignoreCase: true)
                && HasPersistedCompletedTurn(terminal.Home),
            TimeSpan.FromSeconds(120), "The real assistant answer did not complete in the visible TUI.");
        scenario.ExitAndAssertRestored(output, "real model turn");
        using var persistence = new JsonlSessionPersistence(new HarnessHome(terminal.Home).SessionsPath);
        var assistantAnswered = false;
        foreach (var session in persistence.List())
        {
            var handle = persistence.Open(session.Header.Id, SessionAccess.Read);
            try
            {
                assistantAnswered |= handle.Read().Any(entry => entry.Data is AssistantMessagePayload assistant
                    && assistant.Message.Content.OfType<TextBlock>().Any(block =>
                        block.Text.Contains("forty-two", StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                handle.Close();
            }
        }
        Assert.True(assistantAnswered, "The real CLI did not persist the rendered answer as an assistant message.");
    }

    private static bool HasPersistedCompletedTurn(string home)
    {
        using var persistence = new JsonlSessionPersistence(new HarnessHome(home).SessionsPath);
        foreach (var session in persistence.List())
        {
            var handle = persistence.Open(session.Header.Id, SessionAccess.Read);
            try
            {
                if (handle.Read().Any(entry => entry.Data is TurnEndPayload { Reason: TurnEndReason.Completed }))
                    return true;
            }
            finally
            {
                handle.Close();
            }
        }
        return false;
    }

    private static void RequireUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Skip("Kernel termios and Unix PTY session ownership require Linux/macOS.");
    }

    private sealed class Scenario(UnixRawModeTerminal terminal)
    {
        private bool _rawSeen;
        private bool _exitSent;

        public void WaitForRawInput()
        {
            Assert.Equal(7, terminal.InitialMode); // ICANON, ECHO and ISIG before the real executable takes ownership.
            WaitFor(() => terminal.Screen.IsAlternateScreen && terminal.Mode == 0 && terminal.HasRenderedFrame,
                TimeSpan.FromSeconds(90), "The built real CLI did not render its input after taking ownership of the terminal.");
            Assert.True(_rawSeen, "The real CLI never entered raw mode.");
            Assert.Equal(0, terminal.Mode);
        }

        public void ExerciseInputAcrossRefreshAndResize(string probe)
        {
            var emptyInput = SnapshotInputRow();
            var deadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
            for (var length = 1; length <= probe.Length; length++)
            {
                terminal.Write(probe.Substring(length - 1, 1));
                var prefix = probe[..length];
                WaitUntil(() => InputContains(prefix), deadline,
                    "A character sent without Enter did not render in the real input row.");
            }

            // TIOCSWINSZ generates the real foreground-group SIGWINCH, not a synthetic managed resize event.
            terminal.Resize(41, 141);
            WaitUntil(() => InputContains(probe), deadline,
                "Typed input did not survive the larger kernel viewport.");
            terminal.Resize(40, 140);
            WaitUntil(() => InputContains(probe), deadline,
                "Typed input did not survive restoring the kernel viewport.");

            // Cross several background Git refreshes while continuously checking the live kernel input mode.
            var refreshEnd = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
            do { Observe(); } while (Stopwatch.GetTimestamp() < refreshEnd);
            Assert.True(InputContains(probe),
                "A background refresh removed unsent input from the real TUI.");

            for (var length = probe.Length; length > 0; length--)
            {
                terminal.Write("\u007f");
                var oldPrefix = probe[..length];
                var remaining = probe[..(length - 1)];
                WaitUntil(() => length == 1
                    ? InputEquals(emptyInput)
                    : InputContains(remaining) && !InputContains(oldPrefix),
                    deadline, $"Backspace did not remove exactly one unsent input character (previous length {length}).");
            }
        }

        public void ExitAndAssertRestored(ITestOutputHelper output, string description)
        {
            _exitSent = true;
            terminal.Write("/exit\r");
            int? exitCode = null;
            var deadline = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
            WaitUntil(() => (exitCode = terminal.ExitCode()) is not null, deadline,
                "Normal /exit did not terminate the real CLI.");
            Assert.Equal(0, exitCode);
            // The native owner remains alive here on both Unix kernels; Darwin has not revoked the tty.
            var restored = terminal.CompareRestoredTermios();
            Assert.Equal(UnixRawModeTerminal.TermiosFields.None, restored.ChangedFields);
            WaitUntil(() => !terminal.Screen.IsAlternateScreen && terminal.ContainsVisible(UnixRawModeTerminal.MainScreenMarker),
                deadline, "Normal exit did not restore the caller's original main screen.");
            output.WriteLine($"{description}: raw entered; normal exit=0; every termios field restored; original main screen restored; kernel PENDIN before FIONREAD={restored.KernelPendingRetype}");
        }

        public void WaitFor(Func<bool> predicate, TimeSpan budget, string failure)
            => WaitUntil(predicate, Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency), failure);

        private void WaitUntil(Func<bool> predicate, long deadline, string failure)
        {
            do
            {
                Observe();
                if (predicate()) return;
            } while (Stopwatch.GetTimestamp() < deadline);
            Assert.Fail(failure); // Never publish the terminal stream or private configuration on failure.
        }

        private void Observe()
        {
            terminal.Observe();
            if (terminal.Mode == 0) _rawSeen = true;
            if (_rawSeen && !_exitSent)
                Assert.True(terminal.Mode == 0, $"The TUI lost raw input before normal exit (ICANON/ECHO/ISIG bits={terminal.Mode}).");
        }

        private bool InputContains(string marker)
        {
            var input = LayoutEngine.Calculate(terminal.Screen.Width, terminal.Screen.Height).Input;
            return terminal.ContainsVisible(marker, input.Y, input.Y + 1);
        }

        private char[] SnapshotInputRow()
        {
            var input = LayoutEngine.Calculate(terminal.Screen.Width, terminal.Screen.Height).Input;
            var row = terminal.Screen.Row(input.Y);
            var characters = new char[row.Length];
            for (var x = 0; x < row.Length; x++)
                characters[x] = row[x].Character;
            return characters;
        }

        private bool InputEquals(ReadOnlySpan<char> expected)
        {
            var input = LayoutEngine.Calculate(terminal.Screen.Width, terminal.Screen.Height).Input;
            var row = terminal.Screen.Row(input.Y);
            if (row.Length != expected.Length) return false;
            for (var x = 0; x < row.Length; x++)
                if (row[x].Character != expected[x]) return false;
            return true;
        }
    }
}
