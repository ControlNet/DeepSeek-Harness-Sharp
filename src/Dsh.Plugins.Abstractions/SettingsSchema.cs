namespace Dsh.Plugins;

/** 配置项的类型族: 由生成器按声明类型归类, 文档/面板据此选择写法与控件。 */
public enum PluginSettingKind
{
    Text,
    Boolean,
    Integer,
    Number,
    List,
    Mapping,
    Unknown,
}

/** 一个可配置项: 键名/类型/默认值字面量(生成期从声明读出)/说明(可后补)/嵌套子项。 */
public sealed record PluginSettingSpec(
    string Key,
    PluginSettingKind Kind,
    string? DefaultLiteral = null,
    string? Description = null,
    IReadOnlyList<PluginSettingSpec>? Children = null);

/**
 * 插件的配置面: 生成器在各插件程序集里按 ConfigType 产出实现(编译期字面量, 运行期零反射/裁剪安全),
 * 宿主按类型名约定聚合, 供 settings 模板生成、配置校验与可视化编辑共用。
 */
public interface ISettingsSchemaProvider
{
    /** 插件包名(与 [assembly: DshPlugin] 声明一致)。 */
    string Package { get; }

    IReadOnlyList<PluginSettingSpec> Settings { get; }
}

/**
 * 配置面登记表: 生成器在插件程序集的模块初始化器里注册(编译进镜像的与运行期装载的都会跑到),
 * 因此枚举配置面不需要反射、不依赖装载顺序之外的任何状态。重复包名登记按后者覆盖。
 */
public static class DshPluginSettingsRegistry
{
    private static readonly List<ISettingsSchemaProvider> Providers = [];
    private static readonly Lock Gate = new();

    public static void Register(ISettingsSchemaProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate)
        {
            Providers.RemoveAll(existing => string.Equals(existing.Package, provider.Package, StringComparison.Ordinal));
            Providers.Add(provider);
        }
    }

    /** 已注册的配置面(按包名序); 快照返回, 调用方不必持有锁。 */
    public static IReadOnlyList<ISettingsSchemaProvider> All()
    {
        lock (Gate)
            return [.. Providers.OrderBy(provider => provider.Package, StringComparer.Ordinal)];
    }
}

