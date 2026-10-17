using System.Collections.Concurrent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;

namespace Dsh.Host;

/** Shared harness resources and ownership, not a remote domain implementation. */
internal sealed class HarnessRemoteRuntime(HarnessApp app, HostSessionRegistry sessions, HostPendingDecisions pending, HarnessSessionTracker tracker)
{
    private readonly ConcurrentDictionary<string, AgentHandle> _handles = new(StringComparer.Ordinal);
    private readonly Lock _providerGate = new();
    private readonly HashSet<string> _registeredProviders = new(StringComparer.OrdinalIgnoreCase);

    internal HarnessApp App => app;
    internal HostSessionRegistry Sessions => sessions;
    internal HostPendingDecisions Pending => pending;
    internal AgentRegistry Registry => app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)
        ?? throw new InvalidOperationException("AgentRegistry 未注册");
    internal ISessionPersistence? Persistence => app.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, strict: false);

    internal IAgent Require(string sessionId)
        => Registry.Get(SessionId.Create(sessionId)) ?? throw new InvalidOperationException($"unknown session: {sessionId}");

    internal void Own(AgentHandle handle) => _handles[handle.Agent.Id.Value] = handle;

    internal void Track(IAgent agent) => tracker.Track(agent);

    internal void Release(string sessionId)
    {
        if (_handles.TryRemove(sessionId, out var handle))
            handle.Dispose.Dispose();
    }

    internal void DisposeHandles()
    {
        foreach (var handle in _handles.Values)
            handle.Dispose.Dispose();
        _handles.Clear();
    }

    /** Register client-supplied providers before resolving the session's model selection. */
    internal AgentOptions? ResolveAgentOptions(RemoteModelSelection? selection, RemoteProviderSpec? provider)
    {
        if (selection is null || !HasSelection(selection))
            return null;
        if (provider is not null)
            EnsureProviderRegistered(provider);
        return new AgentOptions(selection.Provider, selection.Model, EffortOf(selection));
    }

    private void EnsureProviderRegistered(RemoteProviderSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Id))
            throw new InvalidOperationException("远端模型选择缺少 provider id, 无法注册适配器");
        lock (_providerGate)
        {
            if (_registeredProviders.Contains(spec.Id) || IsProviderRegistered(spec.Id))
                return;
            var settings = new ProviderSettings
            {
                Type = spec.Type,
                Options = new ProviderOptions { BaseUrl = spec.BaseUrl, ApiKey = spec.ApiKey, ApiKeyEnv = spec.ApiKeyEnv },
            };
            var result = ProviderRegistrar.RegisterProvider(app.Ctx, new HarnessOptions(app.Home), spec.Id, settings, isDefault: false);
            if (result.Handle is null)
                throw new InvalidOperationException($"远端 provider \"{spec.Id}\" 注册失败: {result.Error}");
            _registeredProviders.Add(spec.Id);
        }
    }

    private bool IsProviderRegistered(string id)
        => app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false)?.ListProviders()
            .Any(provider => string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase)) ?? false;

    internal static bool HasSelection(RemoteModelSelection selection)
        => !string.IsNullOrWhiteSpace(selection.Provider) && !string.IsNullOrWhiteSpace(selection.Model);

    internal static ReasoningEffortId? EffortOf(RemoteModelSelection selection)
        => string.IsNullOrWhiteSpace(selection.ReasoningEffort) ? null : ReasoningEffortId.Create(selection.ReasoningEffort);
}
