using Dsh.Core;
using Dsh.Host;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** 远端会话必须服从客户端选择: 带选择创建/恢复时在远端注册 provider 并设置 agent.Options(而非远端自身 settings.yaml)。 */
public sealed class RemoteHostModelSelectionTests
{
    private static RemoteProviderSpec Spec => new("client-provider", "openai-compatible", "https://example.invalid", "sk-test");

    private static RemoteModelSelection Selection => new("client-provider", "client-model", "high");

    [Fact]
    public async Task CreateSession_AppliesClientSelectionAndRegistersProvider()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await using var backend = await HarnessRemoteHostBackend.CreateAsync(environment.App, HostPtyDaemonForTests.Unused, TestContext.Current.CancellationToken);
        var llm = environment.App.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;

        var created = await backend.Session.CreateSessionAsync("/work", Selection, Spec, TestContext.Current.CancellationToken);

        Assert.Equal("client-provider", Assert.Single(llm.ListProviders()).Id);
        var agent = environment.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Get(SessionId.Create(created.Id))!;
        Assert.Equal("client-provider", agent.Options.Provider);
        Assert.Equal("client-model", agent.Options.Model);
        Assert.Equal("high", agent.Options.ReasoningEffort?.Value);

        // 同一 provider id 再次注册必须幂等(跳过), 不能因重复注册抛异常。
        var again = await backend.Session.CreateSessionAsync("/work", Selection, Spec, TestContext.Current.CancellationToken);
        Assert.NotEqual(created.Id, again.Id);
        Assert.Single(llm.ListProviders());
    }

    [Fact]
    public async Task ResumeSession_AppliesClientSelectionOverPersistedHeader()
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        // 模拟远端会话已有一条与客户端选择不同的请求头: 恢复时客户端选择必须覆盖它。
        session.Append(new RequestHeaderPayload(
            RequestHeader.Canonicalize(new EpochHeader(new LlmCallConfig("remote-default", "remote-model"))),
            RequestHeaderReasons.Initial,
            true));
        await environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(session);
        var sessionId = session.Id;
        environment = await environment.RestartAsync();

        await using (environment)
        {
            await using var backend = await HarnessRemoteHostBackend.CreateAsync(environment.App, HostPtyDaemonForTests.Unused, TestContext.Current.CancellationToken);
            await backend.Session.ResumeSessionAsync(sessionId.Value, Selection, Spec, TestContext.Current.CancellationToken);

            var llm = environment.App.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
            Assert.Equal("client-provider", Assert.Single(llm.ListProviders()).Id);
            var agent = environment.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Get(sessionId)!;
            Assert.Equal("client-provider", agent.Options.Provider);
            var header = agent.Session.RequestHeader()!;
            Assert.Equal("client-provider", header.Config.Provider);
            Assert.Equal("client-model", header.Config.Model);
        }
    }
}
