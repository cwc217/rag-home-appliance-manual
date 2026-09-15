using Microsoft.AspNetCore.Identity;

namespace HomeApplianceManualRag.Web.Data;

public sealed class ApplicationUser : IdentityUser
{
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public interface ICurrentUser
{
    string? UserId { get; }
    string? Email { get; }
    bool IsAuthenticated { get; }
    bool IsInRole(string role);
}

public sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private System.Security.Claims.ClaimsPrincipal Principal =>
        httpContextAccessor.HttpContext?.User ?? new System.Security.Claims.ClaimsPrincipal();

    public string? UserId => Principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    public string? Email => Principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
        ?? Principal.Identity?.Name;
    public bool IsAuthenticated => Principal.Identity?.IsAuthenticated == true;
    public bool IsInRole(string role) => Principal.IsInRole(role);
}
