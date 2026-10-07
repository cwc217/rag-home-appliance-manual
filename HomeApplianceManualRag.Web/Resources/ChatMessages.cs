namespace HomeApplianceManualRag.Web.Resources;

public static class ChatMessages
{
    public static string NoReadyManualsTitle(string? culture, bool isAuthenticated) =>
        IsChinese(culture)
            ? isAuthenticated ? "尚未有可用手冊" : "目前沒有可用的公開手冊"
            : isAuthenticated ? "No ready manuals yet" : "No public manuals are ready yet.";

    public static string NoReadyManualsDescription(string? culture, bool isAuthenticated) =>
        IsChinese(culture)
            ? isAuthenticated
                ? "上傳家電手冊並等待處理完成，即可在這裡提問。處理中的手冊完成後便會顯示。"
                : "聊天只能使用已就緒的公開手冊。登入以使用帳戶中的手冊，或註冊並上傳手冊。"
            : isAuthenticated
                ? "Upload an appliance manual and wait for processing to finish. Ready manuals will appear here for chat."
                : "Chat uses ready public manuals. Sign in to use manuals in your account, or create an account to upload one.";

    public static string NoReadyManualsAction(string? culture, bool isAuthenticated) =>
        IsChinese(culture)
            ? isAuthenticated ? "管理手冊" : "登入"
            : isAuthenticated ? "Manage manuals" : "Sign in";

    public static string RegisterAction(string? culture) =>
        IsChinese(culture) ? "註冊帳戶" : "Create account";

    public static string NoReadyManualsChip(string? culture) =>
        IsChinese(culture) ? "沒有已就緒的手冊" : "No ready manuals";

    public static string RateLimited(string? culture, int seconds) =>
        IsChinese(culture)
            ? $"請稍候 {seconds} 秒後再試。"
            : $"Too many requests. Try again in {seconds} seconds.";

    public static string NoContext(string? culture) =>
        IsChinese(culture)
            ? "在目前可用的手冊中找不到答案。"
            : "I can't find an answer in the available manuals.";

    public static string QuestionOutOfScope(string? culture) =>
        IsChinese(culture)
            ? "我可以協助處理家電相關問題，請詢問家電操作或故障排除。"
            : "I can help with home-appliance questions. Please ask about operating or troubleshooting an appliance.";

    public static string PromptInjectionBlocked(string? culture) =>
        IsChinese(culture)
            ? "我不能協助繞過指示、索取隱藏資訊或進行未授權的工具操作。"
            : "I can't help bypass instructions, reveal hidden information, or misuse tools.";

    public static string RiskCheckUnavailable(string? culture) =>
        IsChinese(culture)
            ? "目前無法安全地處理這個問題，請稍後再試。"
            : "I can't safely process this question right now. Please try again later.";

    private static bool IsChinese(string? culture) =>
        culture?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;
}
