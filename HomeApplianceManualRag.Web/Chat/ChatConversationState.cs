namespace HomeApplianceManualRag.Web.Chat;

public sealed class ChatConversationState
{
    public event EventHandler? Changed;

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
