using Dsh.AgentAdapters;

namespace Dsh.Tests;

public sealed class AgentAdapterTests
{
    [Fact]
    public void RawTerminal_UsesOnlyItsExplicitCommand()
    {
        var terminal = AgentAdapterCatalog.Terminal("my agent", new AgentCommand("my-agent", ["--flag"]));

        Assert.Equal(new AgentCommand("my-agent", ["--flag"]), terminal.InteractiveCommand("ignored prompt"));
        Assert.Null(terminal.ResumeCommand("id"));
        Assert.Null(terminal.Acp);
    }

    [Fact]
    public void Commands_ExpandPromptAndResumeTemplates()
    {
        Assert.Equal(["fix the tests"], AgentAdapterCatalog.Codex.InteractiveCommand("fix the tests")!.Arguments);
        Assert.Equal(["resume", "abc"], AgentAdapterCatalog.Codex.ResumeCommand("abc")!.Arguments);
        Assert.Equal(["resume", "--last"], AgentAdapterCatalog.Codex.ResumeCommand()!.Arguments);
        Assert.Equal(["--resume", "abc"], AgentAdapterCatalog.ClaudeCode.ResumeCommand("abc")!.Arguments);
        Assert.Equal(["--continue"], AgentAdapterCatalog.ClaudeCode.ResumeCommand()!.Arguments);
        Assert.Equal(["--prompt", "fix the tests"], AgentAdapterCatalog.OpenCode.InteractiveCommand("fix the tests")!.Arguments);
        Assert.Equal(["--session", "ses_1"], AgentAdapterCatalog.OpenCode.ResumeCommand("ses_1")!.Arguments);
        Assert.Equal(new AgentCommand("opencode", ["acp"]), AgentAdapterCatalog.OpenCode.Acp);
        Assert.Empty(AgentAdapterCatalog.OpenCode.InteractiveCommand()!.Arguments);
    }

    [Fact]
    public void GenericAcp_HasNoInteractiveMode()
    {
        var agent = AgentAdapterCatalog.GenericAcp("Gemini", new AgentCommand("gemini", ["--experimental-acp"]));

        Assert.Equal((AgentKinds.Acp, (AgentCommand?)null), (agent.Kind, agent.InteractiveCommand("hi")));
        Assert.Equal("gemini", agent.Acp!.Executable);
    }

    [Fact]
    public void Locator_ResolvesExecutablesOnPathOnly()
    {
        Assert.NotNull(AgentExecutableLocator.Resolve("git"));
        Assert.Null(AgentExecutableLocator.Resolve($"dsh-missing-{Guid.NewGuid():N}"));
        Assert.Null(AgentExecutableLocator.Resolve(Path.Combine(AppContext.BaseDirectory, "missing", "agent")));
    }

    [Fact]
    public async Task Locator_ProbesInstalledAgentVersion()
    {
        if (AgentExecutableLocator.Resolve("opencode") is null)
            Assert.Skip("opencode is not installed on this machine");

        var availability = await AgentExecutableLocator.ProbeAsync(AgentAdapterCatalog.OpenCode, TestContext.Current.CancellationToken);

        Assert.True(availability.CanRunInteractive && availability.CanRunAcp);
        Assert.Matches(@"^\d+\.\d+", availability.Version);
    }
}
