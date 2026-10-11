using HomeApplianceManualRag.Web.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HomeApplianceManualRag.Web.Data;

public static class IdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { ApplicationRoles.Admin, ApplicationRoles.User })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await roles.RoleExistsAsync(role))
            {
                var result = await roles.CreateAsync(new IdentityRole(role));
                if (!result.Succeeded && !await roles.RoleExistsAsync(role))
                    result.ThrowIfFailed();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var options = services.GetRequiredService<IOptions<SeedAdminOptions>>().Value;
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await users.FindByEmailAsync(options.Email);
        if (admin is null)
        {
            admin = new ApplicationUser { UserName = options.Email, Email = options.Email, EmailConfirmed = true };
            (await users.CreateAsync(admin, options.Password)).ThrowIfFailed();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!await users.IsInRoleAsync(admin, ApplicationRoles.Admin))
            (await users.AddToRoleAsync(admin, ApplicationRoles.Admin)).ThrowIfFailed();
    }

    private static void ThrowIfFailed(this IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(x => x.Description)));
    }
}
