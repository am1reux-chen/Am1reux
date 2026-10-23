namespace Lexio.ViewModels;

internal static class ChineseWeekdays
{
    private static readonly string[] LongNames = ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"];
    private static readonly string[] ShortNames = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    public static string LongName(DayOfWeek day) => LongNames[(int)day];
    public static string ShortName(DayOfWeek day) => ShortNames[(int)day];
}
