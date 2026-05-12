using DataSyncManager.Web.Models;
using Microsoft.AspNetCore.Identity;

namespace DataSyncManager.Web.Data;

public static class DbSeeder
{
    public const string RoleViewer = "Viewer";      // Logs & dashboards only
    public const string RoleReadOnly = "ReadOnly";  // Read-only system access
    public const string RoleAdmin = "Admin";        // Full access

    public static async Task SeedAsync(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IConfiguration configuration)
    {
        // Ensure roles exist
        foreach (var role in new[] { RoleViewer, RoleReadOnly, RoleAdmin })
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        // Seed default admin from config / env
        var adminEmail = configuration["Seeding:AdminEmail"] ?? "admin@example.com";
        var adminPassword = configuration["Seeding:AdminPassword"] ?? "Admin@123!";

        if (await userManager.FindByEmailAsync(adminEmail) is null)
        {
            var admin = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                DisplayName = "System Administrator",
                EmailConfirmed = true,
                IsActive = true
            };

            var result = await userManager.CreateAsync(admin, adminPassword);
            if (result.Succeeded)
                await userManager.AddToRoleAsync(admin, RoleAdmin);
        }
    }
}
