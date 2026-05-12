using DataSyncManager.Web.Data;
using DataSyncManager.Web.Models;
using DataSyncManager.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DataSyncManager.Web.Controllers;

public class AccountController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly RoleManager<IdentityRole> _roleManager;

    public AccountController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        RoleManager<IdentityRole> roleManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _roleManager = roleManager;
    }

    // ── Login ────────────────────────────────────────

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Dashboard");
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await _signInManager.PasswordSignInAsync(
            model.Email, model.Password, model.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user is null || !user.IsActive)
            {
                await _signInManager.SignOutAsync();
                ModelState.AddModelError("", "Account is inactive. Contact an administrator.");
                return View(model);
            }
            return LocalRedirect(model.ReturnUrl ?? Url.Action("Index", "Dashboard")!);
        }

        if (result.IsLockedOut)
            ModelState.AddModelError("", "Account locked out. Please try again later.");
        else
            ModelState.AddModelError("", "Invalid email or password.");

        return View(model);
    }

    // ── Register ─────────────────────────────────────

    [HttpGet]
    [Authorize(Roles = DbSeeder.RoleAdmin)]
    public IActionResult Register() => View(new RegisterViewModel());

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = DbSeeder.RoleAdmin)]
    public async Task<IActionResult> Register(RegisterViewModel model, string role)
    {
        if (!ModelState.IsValid) return View(model);
        // Role can come from model.Role (ViewModel property) or the 'role' parameter
        var selectedRole = string.IsNullOrEmpty(model.Role) ? role : model.Role;

        var user = new ApplicationUser
        {
            UserName = model.Email,
            Email = model.Email,
            DisplayName = model.DisplayName,
            EmailConfirmed = true,
            IsActive = true
        };

        var result = await _userManager.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            foreach (var err in result.Errors)
                ModelState.AddModelError("", err.Description);
            return View(model);
        }

        var validRoles = new[] { DbSeeder.RoleViewer, DbSeeder.RoleReadOnly, DbSeeder.RoleAdmin };
        if (validRoles.Contains(selectedRole))
            await _userManager.AddToRoleAsync(user, selectedRole);
        else
            await _userManager.AddToRoleAsync(user, DbSeeder.RoleViewer);

        TempData["Success"] = $"User {model.Email} created successfully.";
        return RedirectToAction(nameof(Users));
    }

    // ── Users ─────────────────────────────────────────

    [HttpGet]
    [Authorize(Roles = DbSeeder.RoleAdmin)]
    public async Task<IActionResult> Users()
    {
        var users = await _userManager.Users.ToListAsync();
        var list = new List<UserListViewModel>();
        foreach (var u in users)
        {
            var roles = await _userManager.GetRolesAsync(u);
            list.Add(new UserListViewModel
            {
                Id = u.Id,
                Email = u.Email ?? "",
                DisplayName = u.DisplayName,
                IsActive = u.IsActive,
                Role = roles.FirstOrDefault() ?? "None",
                CreatedAt = u.CreatedAt
            });
        }
        return View(list);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = DbSeeder.RoleAdmin)]
    public async Task<IActionResult> ToggleUser(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null) return NotFound();
        user.IsActive = !user.IsActive;
        await _userManager.UpdateAsync(user);
        return RedirectToAction(nameof(Users));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = DbSeeder.RoleAdmin)]
    public async Task<IActionResult> ChangeRole(string id, string newRole)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null) return NotFound();

        var existingRoles = await _userManager.GetRolesAsync(user);
        await _userManager.RemoveFromRolesAsync(user, existingRoles);

        var validRoles = new[] { DbSeeder.RoleViewer, DbSeeder.RoleReadOnly, DbSeeder.RoleAdmin };
        if (validRoles.Contains(newRole))
            await _userManager.AddToRoleAsync(user, newRole);

        TempData["Success"] = $"Role updated for {user.Email}.";
        return RedirectToAction(nameof(Users));
    }

    // ── Logout ───────────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    // ── Change Password ──────────────────────────────

    [HttpGet, Authorize]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [HttpPost, Authorize, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var result = await _userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var err in result.Errors)
                ModelState.AddModelError("", err.Description);
            return View(model);
        }

        TempData["Success"] = "Password changed successfully.";
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpGet]
    public IActionResult AccessDenied() => View();
}
