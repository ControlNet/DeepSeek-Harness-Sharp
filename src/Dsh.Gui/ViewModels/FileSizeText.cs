using System.Globalization;

namespace Dsh.Gui.ViewModels;

/** 文件视图的字节数与修改时间显示文字。 */
public static class FileSizeText
{
    private const double Step = 1024;
    private const string TimeFormat = "yyyy-MM-dd HH:mm";

    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /** 1023 B, 1.5 KB, 12.0 MB ...(1024 进制)。 */
    public static string Format(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= Step && unit < Units.Length - 1)
        {
            value /= Step;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value.ToString("0.0", CultureInfo.InvariantCulture)} {Units[unit]}";
    }

    /** Unix 毫秒 → 本地时间文字。 */
    public static string Time(long unixMilliseconds)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).ToLocalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);
}
