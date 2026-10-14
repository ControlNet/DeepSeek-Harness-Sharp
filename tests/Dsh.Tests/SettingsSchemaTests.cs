using Dsh.Plugins;

namespace Dsh.Tests;

/** 配置面生成: 插件声明 ConfigType 后, 生成器按声明类型产出 ISettingsSchemaProvider(字面量, 零反射)。 */
public sealed class SettingsSchemaTests
{
    [Fact]
    public void Generated_Schema_Lists_Declared_Keys_With_Defaults()
    {
        var schema = new Dsh.Toon.Generated.DshPluginSettingsSchema();

        Assert.Equal("@deepseek-ai/dsh-toon", schema.Package);
        Assert.Equal(["output", "input"], schema.Settings.Select(setting => setting.Key));
        Assert.Equal(PluginSettingKind.Boolean, schema.Settings[0].Kind);
        Assert.Equal("true", schema.Settings[0].DefaultLiteral);
        Assert.Equal(PluginSettingKind.List, schema.Settings[1].Kind);
        Assert.Null(schema.Settings[1].DefaultLiteral);
    }

    /** 嵌套配置(记录属性)递归成子项: 宿主才能校验 mongo.connectionString 这类路径。 */
    [Fact]
    public void Generated_Schema_Recurses_Into_Nested_Config_Records()
    {
        var schema = new Dsh.Memory.Generated.DshPluginSettingsSchema();

        Assert.Equal("@deepseek-ai/dsh-memory", schema.Package);
        Assert.Equal(["backend", "file", "capture", "mongo"], schema.Settings.Select(setting => setting.Key));
        var mongo = Assert.Single(schema.Settings, setting => setting.Key == "mongo");
        Assert.Equal(PluginSettingKind.Mapping, mongo.Kind);
        Assert.Equal(["connectionString", "database", "collection", "key"], mongo.Children!.Select(setting => setting.Key));
        Assert.Equal("\"dsh_memory\"", mongo.Children![1].DefaultLiteral);
    }

    /** 模块初始化器把配置面登记进注册表: 宿主枚举不需要反射、不依赖装载顺序。 */
    [Fact]
    public void Module_Initializer_Registers_The_Schema()
    {
        _ = new Dsh.Toon.Generated.DshPluginSettingsSchema();

        var registered = DshPluginSettingsRegistry.All();
        Assert.Contains(registered, provider => provider.Package == "@deepseek-ai/dsh-toon");
        Assert.Equal(["output", "input"],
            Assert.Single(registered, provider => provider.Package == "@deepseek-ai/dsh-toon").Settings.Select(setting => setting.Key));
    }
}
