using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.SessionQuery;

namespace Dsh.Tests;

/** Legacy ACP conversations enter the same persistence, query and subscription paths as harness conversations. */
public sealed class AcpSessionPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyConversation_IsImportedAndQueryable_WithTheSameIdentityAcrossRestart(bool interruptedImport)
    {
        await using var host = await HarnessHostFixture.CreateAsync("asp", Ct);
        await host.StopHostAsync();
        var source = Conversation(host.Environment.App.Home.Root);
        var original = await WriteLegacyAsync(host, source);
        var persistence = host.Environment.App.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)!;
        if (interruptedImport)
        {
            using var partial = persistence.Create(source.Header);
            partial.Append(source.SnapshotEvents(0, 2));
            partial.Flush();
        }

        await host.StartHostAsync(Ct);
        var client = await host.ConnectAsync("reader", Ct);
        var last = source.SnapshotEvents()[^1];
        var replay = await RemoteHostMultiClientTests.NextAsync(client.SubscribeAsync(source.Id.Value, last.Seq, Ct));
        Assert.Equal(DshJson.Serialize(last), replay.EventJson);
        Assert.Equal(source.Id.Value, replay.SessionId);
        Assert.Equal(source.Header.Cwd, persistence.Stat(source.Id)!.Header.Cwd);
        using (var stored = persistence.Open(source.Id, SessionAccess.Read))
            Assert.Equal(source.SnapshotEvents().Select(item => DshJson.Serialize(item)), stored.Read(0, source.Seq).Select(item => DshJson.Serialize(item)));
        Assert.Equal(original, await File.ReadAllTextAsync($"{LegacyPath(host, source.Id)}.imported", Ct));
        Assert.False(File.Exists(LegacyPath(host, source.Id)));

        // With the host's live sessions released, search must discover the conversation through the real persistence consumer.
        await host.StopHostAsync();
        using (var query = new SessionQueryService(host.Environment.App.Ctx))
            Assert.Contains(query.Search("transcriptneedle"), hit => hit.SessionId == source.Id.Value);
        await host.RestartHostAsync(Ct);
        var after = await host.ConnectAsync("after", Ct);
        var restored = await RemoteHostMultiClientTests.NextAsync(after.SubscribeAsync(source.Id.Value, last.Seq, Ct));
        Assert.Equal(replay.EventJson, restored.EventJson);
        var reopened = host.Environment.App.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)!;
        Assert.Single(reopened.List(), snapshot => snapshot.Header.Id == source.Id);
        using var handle = reopened.Open(source.Id, SessionAccess.Read);
        Assert.Equal(source.SnapshotEvents().Select(item => DshJson.Serialize(item)), handle.Read(0, source.Seq).Select(item => DshJson.Serialize(item)));
        Assert.Equal(original, await File.ReadAllTextAsync($"{LegacyPath(host, source.Id)}.imported", Ct));
    }

    [Fact(Timeout = 60000)]
    public async Task InvalidLegacyConversation_LeavesItsSourceUntouched()
    {
        await using var host = await HarnessHostFixture.CreateAsync("asf", Ct);
        await host.StopHostAsync();
        var source = Conversation(host.Environment.App.Home.Root);
        await WriteLegacyAsync(host, source);
        var path = LegacyPath(host, source.Id);
        const string invalid = "{broken\n{\"not\":\"an event\"}\n";
        await File.WriteAllTextAsync(path, invalid, Ct);

        await Assert.ThrowsAsync<JsonException>(() => host.StartHostAsync(Ct));

        Assert.Equal(invalid, await File.ReadAllTextAsync(path, Ct));
        Assert.Null(host.Environment.App.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)!.Stat(source.Id));
        Assert.False(File.Exists($"{path}.imported"));
    }

    private static Session Conversation(string cwd)
    {
        var session = Session.Create(SessionId.Create("acp-storage-history"), header: new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = SessionId.Create("acp-storage-history"),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Cwd = cwd,
            Title = "ACP history",
            IsSeeded = false,
        });
        session.Append(new TurnStartPayload(1));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("transcriptneedle")), new SurfaceOp.Append());
        session.Append(new AssistantChunkPayload(1, 0, new StreamChunk.ReasoningDelta(0, "checking")));
        session.Append(new AssistantChunkPayload(1, 0, new StreamChunk.TextDelta(0, "answer")));
        session.Append(new AssistantMessagePayload(1, 0, MessageFactory.CreateAssistantMessage([new TextBlock("answer")], "agent", "agent")), new SurfaceOp.Append());
        var call = ToolCallId.Create("call-1");
        session.Append(new ToolCallPayload(1, 1, call, "read", "{}"));
        session.Append(new ToolResultPayload(1, 1, MessageFactory.CreateToolResultMessage(call, [new TextBlock("result")], false), null), new SurfaceOp.Append());
        session.Append(new SystemMessagePayload(1, 1, MessageFactory.CreateSystemMessage([new TextBlock("[completed] read")], new PluginMessageSource("acp-plan"))), new SurfaceOp.Append());
        session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        return session;
    }

    private static async Task<string> WriteLegacyAsync(HarnessHostFixture host, Session source)
    {
        var path = LegacyPath(host, source.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = string.Join('\n', source.SnapshotEvents().Select(item => DshJson.Serialize(item))) + "\n";
        await File.WriteAllTextAsync(path, original, Ct);
        var records = new JsonArray(new JsonObject
        {
            ["SessionId"] = source.Id.Value,
            ["Mode"] = "acp",
            ["AgentKind"] = "acp",
            ["Executable"] = "opencode",
            ["Arguments"] = new JsonArray("acp"),
            ["ProjectPath"] = source.Header.Cwd,
            ["Title"] = source.Header.Title,
            ["AcpSessionId"] = "agent-session-preserved",
        });
        await File.WriteAllTextAsync(Path.Combine(host.Environment.App.Home.HostPath, "agents.json"), records.ToJsonString(), Ct);
        return original;
    }

    private static string LegacyPath(HarnessHostFixture host, SessionId id)
        => Path.Combine(host.Environment.App.Home.HostPath, "agents", $"{id.Value}.events.jsonl");
}
