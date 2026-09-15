namespace HomeApplianceManualRag.Web.Resources;

public static class IdentityMessages
{
    public static string Required(string field, string? culture) =>
        IsChinese(culture) ? $"{field}為必填欄位。" : $"{field} is required.";
    public static string InvalidCredentials(string? culture) =>
        IsChinese(culture) ? "電子郵件或密碼不正確。" : "Invalid email or password.";
    public static string RegistrationFailed(string? culture) =>
        IsChinese(culture) ? "註冊失敗，請檢查輸入資料。" : "Registration failed. Please check your information.";
    public static string EmailTaken(string? culture) =>
        IsChinese(culture) ? "此電子郵件已註冊。" : "This email is already registered.";
    private static bool IsChinese(string? culture) => culture?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;
}
