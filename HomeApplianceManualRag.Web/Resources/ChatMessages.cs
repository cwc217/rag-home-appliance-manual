namespace HomeApplianceManualRag.Web.Resources;

public static class ChatMessages
{
    public static string NoReadyManualsTitle(string? culture, bool isAuthenticated) =>
        UiText.Get(
            isAuthenticated ? "No ready manuals yet" : "No public manuals are ready yet.",
            culture);

    public static string NoReadyManualsDescription(string? culture, bool isAuthenticated) =>
        UiText.Get(
            isAuthenticated
                ? "Upload an appliance manual and wait for processing to finish. Ready manuals will appear here for chat."
                : "Chat uses ready public manuals. Sign in to use manuals in your account, or create an account to upload one.",
            culture);

    public static string NoReadyManualsAction(string? culture, bool isAuthenticated) =>
        UiText.Get(isAuthenticated ? "Manage manuals" : "Sign in", culture);

    public static string RegisterAction(string? culture) =>
        UiText.Get("Create account", culture);

    public static string NoReadyManualsChip(string? culture) =>
        UiText.Get("No ready manuals", culture);

    public static string RateLimited(string? culture, int seconds) =>
        UiText.Format("Too many requests. Try again in {0} seconds.", culture, seconds);

    public static string NoContext(string? culture) =>
        UiText.Get("I can't find an answer in the available manuals.", culture);

    public static string QuestionOutOfScope(string? culture) =>
        UiText.Get(
            "I can help with home-appliance questions. Please ask about operating or troubleshooting an appliance.",
            culture);

    public static string PromptInjectionBlocked(string? culture) =>
        UiText.Get("I can't help bypass instructions, reveal hidden information, or misuse tools.", culture);

    public static string RiskCheckUnavailable(string? culture) =>
        UiText.Get("I can't safely process this question right now. Please try again later.", culture);
}
