using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 新写路径写出的日志交给不认识插件事件类型的版本读取。
 * 读取方在独立的 AssemblyLoadContext 里重新装载 Dsh.* 程序集，其静态 codec 表只有核心类型，等同于没装该插件的旧版本。
 */
public sealed class SessionLogCompatibilityTests : IDisposable
{
    private const string IgnorableType = "compat-probe/annotation";
    private const string RequiredType = "compat-probe/required";
    private const string UnknownPayloadName = nameof(UnknownSessionEventPayload);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dsh-compat-{Guid.NewGuid():N}");

    static SessionLogCompatibilityTests()
    {
        ProbePayload.Register(IgnorableType, ignorable: true);
        ProbePayload.Register(RequiredType, ignorable: false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Append_MarksOnlyTypesRegisteredAsIgnorable()
    {
        var session = Session.Create(SessionId.Create(Guid.NewGuid().ToString("N")));

        Assert.False(session.Append(new TurnStartPayload(1)).Ignorable);
        Assert.True(session.Append(new ProbePayload(IgnorableType, "note")).Ignorable);
        Assert.False(session.Append(new ProbePayload(RequiredType, "note")).Ignorable);
    }

    [Fact]
    public void Register_RefusesIgnorableSurfaceType()
    {
        Assert.Throws<ArgumentException>(() => ProbePayload.Register(SessionEventTypes.UserMessage, ignorable: true));
    }

    [Fact]
    public void ReaderWithoutTheType_SkipsIgnorableEvent()
    {
        var id = WriteLog(new ProbePayload(IgnorableType, "annotation"));

        var events = LegacyRead(id);

        Assert.Equal(
            [
                (SessionEventTypes.TurnStart, nameof(TurnStartPayload)),
                (IgnorableType, UnknownPayloadName),
                (SessionEventTypes.TurnEnd, nameof(TurnEndPayload)),
            ],
            events);
    }

    [Fact]
    public void ReaderWithoutTheType_RefusesRequiredEvent()
    {
        var id = WriteLog(new ProbePayload(RequiredType, "state change"));

        var error = Assert.Throws<TargetInvocationException>(() => LegacyRead(id));

        Assert.Contains($"\"{RequiredType}\"", error.InnerException?.Message);
        Assert.Contains("not marked ignorable", error.InnerException?.Message);
    }

    private SessionHeader NewHeader() => new()
    {
        Version = SessionHeader.SessionFormatVersion,
        Id = SessionId.Create(Guid.NewGuid().ToString("N")),
        CreatedAt = 1700000000000,
        Cwd = _root,
        IsSeeded = false,
    };

    private SessionId WriteLog(ProbePayload probe)
    {
        var header = NewHeader();
        var id = header.Id;
        var session = Session.Create(id, header: header);
        session.Append(new TurnStartPayload(1));
        session.Append(probe);
        session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        using var persistence = new JsonlSessionPersistence(_root);
        using var handle = persistence.Create(header);
        handle.Append(session.SnapshotEvents());
        handle.Close();
        return id;
    }

    /** 在隔离的加载上下文里走持久化读路径，返回每个事件的类型与解出的 payload 类名。 */
    private List<(string Type, string Payload)> LegacyRead(SessionId id)
    {
        var context = new LegacyHarnessContext();
        try
        {
            var core = context.LoadFromAssemblyName(new AssemblyName("Dsh.Core"));
            Assert.NotSame(typeof(SessionEventCodec).Assembly, core);
            var codec = core.GetType("Dsh.Core.SessionEventCodec", throwOnError: true)!;
            Assert.False((bool)codec.GetMethod(nameof(SessionEventCodec.IsRegistered))!.Invoke(null, [IgnorableType])!);
            var persistenceAssembly = context.LoadFromAssemblyName(new AssemblyName("Dsh.Persistence"));
            var persistenceType = persistenceAssembly.GetType(typeof(JsonlSessionPersistence).FullName!, throwOnError: true)!;
            var constructor = persistenceType.GetConstructors().Single();
            var arguments = constructor.GetParameters().Select((parameter, index) => index == 0 ? _root : parameter.DefaultValue).ToArray();
            var persistence = (IDisposable)constructor.Invoke(arguments);
            using (persistence)
            {
                var sessionIdType = context.LoadFromAssemblyName(new AssemblyName("Dsh.Llm")).GetType(typeof(SessionId).FullName!, throwOnError: true)!;
                var legacyId = sessionIdType.GetMethod(nameof(SessionId.Create))!.Invoke(null, [id.Value]);
                var access = Enum.Parse(core.GetType(typeof(SessionAccess).FullName!, throwOnError: true)!, nameof(SessionAccess.Read));
                var open = core.GetType(typeof(ISessionPersistence).FullName!, throwOnError: true)!.GetMethod(nameof(ISessionPersistence.Open))!;
                using var handle = (IDisposable)open.Invoke(persistence, [legacyId, access])!;
                var read = core.GetType(typeof(ISessionHandle).FullName!, throwOnError: true)!.GetMethod(nameof(ISessionHandle.Read))!;
                var events = (IEnumerable)read.Invoke(handle, [0L, null])!;
                return [.. events.Cast<object>().Select(Describe)];
            }
        }
        finally
        {
            context.Unload();
        }
    }

    private static (string Type, string Payload) Describe(object sessionEvent)
    {
        var type = sessionEvent.GetType();
        return ((string)type.GetProperty(nameof(SessionEvent.Type))!.GetValue(sessionEvent)!,
            type.GetProperty(nameof(SessionEvent.Data))!.GetValue(sessionEvent)!.GetType().Name);
    }

    private sealed class LegacyHarnessContext() : AssemblyLoadContext("legacy-harness", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName.Name}.dll");
            return assemblyName.Name?.StartsWith("Dsh.", StringComparison.Ordinal) == true && File.Exists(path)
                ? LoadFromAssemblyPath(path)
                : null;
        }
    }

    private sealed record ProbePayload(string EventType, string Note) : SessionEventPayload
    {
        public override string Type => EventType;

        public static void Register(string type, bool ignorable)
            => SessionEventCodec.Register(
                type,
                (element, _) => new ProbePayload(type, element.GetProperty("note").GetString() ?? ""),
                static (ProbePayload payload, Utf8JsonWriter writer, JsonSerializerOptions _) =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("note", payload.Note);
                    writer.WriteEndObject();
                },
                ignorable);
    }
}
