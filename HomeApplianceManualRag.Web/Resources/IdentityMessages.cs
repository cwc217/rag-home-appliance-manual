namespace HomeApplianceManualRag.Web.Resources;

public static class IdentityMessages
{
    public static string Required(string field, string? culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        var localizedField = culture?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true
            ? field switch
            {
                "Email" => UiText.Get("Email", culture),
                "Email and password" => UiText.Get("Email and password", culture),
                _ => field
            }
            : field;

        return UiText.Format("{0} is required.", culture, localizedField);
    }

    public static string InvalidCredentials(string? culture) =>
        UiText.Get("Invalid email or password.", culture);

    public static string RegistrationFailed(string? culture) =>
        UiText.Get("Registration failed. Please check your information.", culture);

    public static string EmailTaken(string? culture) =>
        UiText.Get("This email is already registered.", culture);
}
