namespace Dsh.Boot;

/**
 * settings.yaml 模板与配置面的同步钩子: 模板里 `# >>> generated` / `# <<< generated` 之间的内容由
 * SettingsSchemaCatalog 渲染, 手写说明在块外不受影响。
 * 用法: 构建/CI 用 Verify(不一致即失败并给出修复命令), 开发用 Write 一键重写。
 */
public static class SettingsTemplate
{
    /** 模板随程序发布: 默认指向输出目录里的那份副本(verify 用); 写回源码用 --path 指定。 */
    public static string DefaultPath
        => Path.Combine(AppContext.BaseDirectory, "Profiles", "Templates", "settings.yaml");

    public static string FixHint => "dsharp config schema --write-template --path <模板路径>";

    /** 校验: 返回 null 表示同步, 否则返回给用户的说明(含修复命令)。 */
    public static string? Verify(string path)
    {
        if (!File.Exists(path))
            return $"模板不存在: {path}";
        var text = File.ReadAllText(path);
        if (!TryFindBlock(text, out var begin, out var tail))
            return $"模板缺少生成块标记({SettingsSchemaCatalog.GeneratedBegin} / {SettingsSchemaCatalog.GeneratedEnd}): {path}";
        var expected = SettingsSchemaCatalog.RenderTemplateBlock();
        // 与写回同一区域口径: 多出来的重复结束标记也算不同步(写回一次即自愈)。
        var actual = text[begin..tail];
        return Normalize(actual) == Normalize(expected)
            ? null
            : $"settings.yaml 模板与配置面不同步(新增/改动了配置项后未更新模板): {path}; 修复: {FixHint}";
    }

    /** 写回: 就地替换生成块(并吞掉历史版本留下的重复结束标记), 返回是否有改动。 */
    public static bool Write(string path)
    {
        var text = File.ReadAllText(path);
        if (!TryFindBlock(text, out var begin, out var tail))
            throw new InvalidOperationException($"模板缺少生成块标记({SettingsSchemaCatalog.GeneratedBegin} / {SettingsSchemaCatalog.GeneratedEnd}): {path}");
        var updated = string.Concat(text[..begin], SettingsSchemaCatalog.RenderTemplateBlock(), text[tail..]);
        if (Normalize(updated) == Normalize(text))
            return false;
        File.WriteAllText(path, updated);
        return true;
    }

    /**
     * 定位生成块: begin 为起始标记下标, tail 为"块之后正文"的起点。
     * tail 会跳过结束标记本身以及紧跟在后面的重复结束标记(曾有写回 bug 留下多份), 因此写回一次即自愈。
     */
    private static bool TryFindBlock(string text, out int begin, out int tail)
    {
        begin = text.IndexOf(SettingsSchemaCatalog.GeneratedBegin, StringComparison.Ordinal);
        tail = 0;
        if (begin < 0)
            return false;
        var end = text.IndexOf(SettingsSchemaCatalog.GeneratedEnd, begin, StringComparison.Ordinal);
        if (end < 0)
            return false;
        tail = end + SettingsSchemaCatalog.GeneratedEnd.Length;
        while (true)
        {
            var cursor = tail;
            while (cursor < text.Length && (text[cursor] == '\r' || text[cursor] == '\n'))
                cursor++;
            if (cursor + SettingsSchemaCatalog.GeneratedEnd.Length > text.Length
                || string.CompareOrdinal(text, cursor, SettingsSchemaCatalog.GeneratedEnd, 0, SettingsSchemaCatalog.GeneratedEnd.Length) != 0)
            {
                break;
            }
            tail = cursor + SettingsSchemaCatalog.GeneratedEnd.Length;
        }
        return true;
    }

    private static string Normalize(string text)
        => text.Replace("\r\n", "\n").TrimEnd();
}
