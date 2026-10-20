using Dsh.Boot;

namespace Dsh.Tests;

/**
 * settings.yaml 模板与配置面同步门禁: 任何"加了配置项/改了插件参数"的改动若没同步模板, 这里就会红。
 * 修复: dsharp config schema --write-template --path src/Dsh.Boot/Profiles/Templates/settings.yaml
 */
[Collection(RegistryCollection)]
public sealed class SettingsTemplateTests
{
    /** Tests that load plugins with settings share the global settings registry with these checks, so they never overlap. */
    public const string RegistryCollection = "PluginSettingsRegistry";

    /** 随包发布的输出副本(AppContext.BaseDirectory/Profiles/Templates/settings.yaml)。 */
    [Fact]
    public void Output_Template_Is_In_Sync_With_Schema()
    {
        Assert.Null(SettingsTemplate.Verify(SettingsTemplate.DefaultPath));
    }

    /** 源码模板(用户克隆仓库时看到的那份): 与生成块一致, 免得只在发布产物上同步。 */
    [Fact]
    public void Source_Template_Is_In_Sync_With_Schema()
    {
        var path = SourceTemplatePath();
        Assert.Null(SettingsTemplate.Verify(path));
    }


    private static string SourceTemplatePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Dsh.Boot", "Profiles", "Templates", "settings.yaml");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("找不到源码里的 settings.yaml 模板");
    }
}
