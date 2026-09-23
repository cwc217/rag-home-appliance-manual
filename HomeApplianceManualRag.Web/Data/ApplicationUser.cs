using Microsoft.AspNetCore.Identity;

namespace HomeApplianceManualRag.Web.Data;

public sealed class ApplicationUser : IdentityUser
{
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool AccountDisabled { get; set; }
    public DateTime? DisabledAtUtc { get; set; }
    public string? DisabledByUserId { get; set; }
}

public interface ICurrentUser
{
    string? UserId { get; }
    string? Email { get; }
    bool IsAuthenticated { get; }
    bool IsInRole(string role);
}

public sealed class CurrentUser : ICurrentUser
{
    private readonly System.Security.Claims.ClaimsPrincipal principal;

    public CurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        principal = httpContextAccessor.HttpContext?.User
            ?? new System.Security.Claims.ClaimsPrincipal();
    }

    public string? UserId => principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    public string? Email => principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
        ?? principal.Identity?.Name;
    public bool IsAuthenticated => principal.Identity?.IsAuthenticated == true;
    public bool IsInRole(string role) => principal.IsInRole(role);
}
