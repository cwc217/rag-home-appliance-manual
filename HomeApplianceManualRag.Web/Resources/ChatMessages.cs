namespace HomeApplianceManualRag.Web.Resources;

public static class ChatMessages
{
    public static string RateLimited(string? culture, int seconds) =>
        IsChinese(culture)
            ? $"請稍候 {seconds} 秒後再試。"
            : $"Too many requests. Try again in {seconds} seconds.";

    public static string NoContext(string? culture) =>
        IsChinese(culture)
            ? "在目前可用的手冊中找不到答案。"
            : "I can't find an answer in the available manuals.";

    private static bool IsChinese(string? culture) =>
        culture?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;
}
