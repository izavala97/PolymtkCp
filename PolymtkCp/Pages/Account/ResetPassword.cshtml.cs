using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PolymtkCp.Pages.Account;

public class ResetPasswordModel : PageModel
{
    private readonly Supabase.Client _supabase;
    private readonly ILogger<ResetPasswordModel> _logger;

    public ResetPasswordModel(Supabase.Client supabase, ILogger<ResetPasswordModel> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    [BindProperty]
    public string AccessToken { get; set; } = string.Empty;

    [BindProperty]
    [Required, MinLength(8, ErrorMessage = "Password must be at least 8 characters.")]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    [Required, Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            ErrorMessage = "Invalid or expired reset link. Please request a new one.";
            return Page();
        }

        try
        {
            // Restore the session from the token so we can update the password
            await _supabase.Auth.SetSession(AccessToken, AccessToken);
            await _supabase.Auth.Update(new Supabase.Gotrue.UserAttributes { Password = Password });

            _logger.LogInformation("Password updated successfully.");
            SuccessMessage = "Your password has been updated. ";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Password reset failed.");
            ErrorMessage = "The reset link is invalid or has expired. Please request a new one.";
        }

        return Page();
    }
}
