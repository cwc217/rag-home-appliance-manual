using Microsoft.AspNetCore.Identity;

namespace HomeApplianceManualRag.Web.Data;

public sealed class ApplicationUser : IdentityUser
{
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
