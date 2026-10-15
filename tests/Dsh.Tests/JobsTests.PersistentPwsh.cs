using Dsh.Core;
using Dsh.Jobs;

namespace Dsh.Tests;

public partial class JobsTests
{
    [Collection(SerialProcessCollection.Name)]
    public sealed class PersistentPwsh : IDisposable
    {
        private readonly JobsTests _outer = new();

        public PersistentPwsh()
        {
            PersistentPwshTool.Register(_outer._ctx, new PersistentPwshConfig { TimeoutMs = 20_000 });
        }

        public void Dispose() => _outer.Dispose();

        private static void RequirePwsh()
        {
            if (!ShellProbe.Pwsh)
                Assert.Skip("pwsh is unavailable; skipping the persistent PowerShell tests");
        }

        private async Task<string> Pwsh(IAgent agent, string command)
        {
            var result = await _outer.Execute(PersistentPwshTool.ToolName, new { command }, agent);
            Assert.False(result.IsError, TextOf(result));
            return TextOf(result);
        }

        [Fact]
        public async Task LocationVariablesFunctionsAndEnvironment_PersistAcrossCalls()
        {
            RequirePwsh();
            Directory.CreateDirectory(Path.Combine(_outer._tempDir, "sub"));
            var agent = _outer.NewAgent();

            await Pwsh(agent, "Set-Location sub");
            Assert.EndsWith("sub", (await Pwsh(agent, "(Get-Location).Path")).Trim());

            await Pwsh(agent, "$answer = 41; function Get-Answer { $answer + 1 }");
            Assert.Equal("42", (await Pwsh(agent, "Get-Answer")).Trim());

            await Pwsh(agent, "$env:DSH_TEST_MARKER = 'hello'");
            Assert.Equal("hello", (await Pwsh(agent, "$env:DSH_TEST_MARKER")).Trim());
        }

        [Fact]
        public async Task MultiLineCommandWithQuotesAndNonAscii_RunsAsOneCommand()
        {
            RequirePwsh();
            var agent = _outer.NewAgent();

            var text = await Pwsh(agent, "$a = 1\n$b = 2\n\"sum=$($a + $b) it's \"\"quoted\"\" 你好\"");

            Assert.Equal("sum=3 it's \"quoted\" 你好", text.Trim());
        }

        [Fact]
        public async Task ErrorAsLastStatement_IsMergedAndReportedAsExitOne()
        {
            RequirePwsh();
            var agent = _outer.NewAgent();

            var text = await Pwsh(agent, "Write-Error 'boom'");

            Assert.Contains("boom", text);
            Assert.EndsWith("[exit code: 1]", text);
        }

        [Fact]
        public async Task ErrorFollowedBySuccess_HasNoExitMarker()
        {
            RequirePwsh();
            var agent = _outer.NewAgent();

            var text = await Pwsh(agent, "Write-Error 'boom'; 'ok'");

            Assert.Contains("boom", text);
            Assert.Contains("ok", text);
            Assert.DoesNotContain("[exit code:", text);
        }

        [Fact]
        public async Task FailedNativeCommand_ReportsItsExitCode()
        {
            RequirePwsh();
            var agent = _outer.NewAgent();

            var text = await Pwsh(agent, "pwsh -NoLogo -NoProfile -NonInteractive -Command 'exit 4'");

            Assert.EndsWith("[exit code: 4]", text);
        }

        [Fact]
        public async Task TerminatingError_ReportsExitOneAndKeepsTheSession()
        {
            RequirePwsh();
            var agent = _outer.NewAgent();
            await Pwsh(agent, "$kept = 'still here'");

            var text = await Pwsh(agent, "throw 'bad thing'");
            Assert.Contains("bad thing", text);
            Assert.EndsWith("[exit code: 1]", text);

            Assert.Equal("still here", (await Pwsh(agent, "$kept")).Trim());
        }

        [Fact]
        public async Task Exit_ResetsAndNextCallStartsFresh()
        {
            RequirePwsh();
            Directory.CreateDirectory(Path.Combine(_outer._tempDir, "sub"));
            var agent = _outer.NewAgent();
            await Pwsh(agent, "Set-Location sub");

            var exited = await Pwsh(agent, "exit 3");
            Assert.Contains("[shell exited: code 3]", exited);
            Assert.Contains("persistent pwsh shell was reset", exited);

            Assert.DoesNotContain("sub", await Pwsh(agent, "(Get-Location).Path"));
        }

        [Fact]
        public async Task Timeout_ResetsSession()
        {
            RequirePwsh();
            using var outer = new JobsTests();
            PersistentPwshTool.Register(outer._ctx, new PersistentPwshConfig { TimeoutMs = 5_000 });
            var agent = outer.NewAgent();

            var slow = await outer.Execute(PersistentPwshTool.ToolName, new { command = "Start-Sleep -Seconds 30" }, agent);
            Assert.False(slow.IsError);
            Assert.Contains("timed out after 5 seconds", TextOf(slow));
            Assert.Contains("persistent pwsh shell was reset", TextOf(slow));

            var alive = await outer.Execute(PersistentPwshTool.ToolName, new { command = "'alive'" }, agent);
            Assert.False(alive.IsError);
            Assert.Contains("alive", TextOf(alive));
        }
    }
}
