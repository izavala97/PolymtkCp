using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PolymtkCp.Pages.Account;

public class ForgotPasswordModel : PageModel
{
    private readonly Supabase.Client _supabase;
    private readonly ILogger<ForgotPasswordModel> _logger;

    public ForgotPasswordModel(Supabase.Client supabase, ILogger<ForgotPasswordModel> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    [BindProperty]
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        try
        {
            await _supabase.Auth.ResetPasswordForEmail(Email);
            _logger.LogInformation("Password reset email sent to {Email}.", Email);
            // Always show success to avoid email enumeration
            SuccessMessage = "If an account exists for that email, a reset link has been sent.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Password reset request failed for {Email}.", Email);
            // Still show generic success to avoid email enumeration
            SuccessMessage = "If an account exists for that email, a reset link has been sent.";
        }

        return Page();
    }
}
