using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PolymtkCp.Pages.Account;

public class RegisterModel : PageModel
{
    private readonly Supabase.Client _supabase;
    private readonly ILogger<RegisterModel> _logger;

    public RegisterModel(Supabase.Client supabase, ILogger<RegisterModel> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    [BindProperty]
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    [Required, MinLength(8, ErrorMessage = "Password must be at least 8 characters.")]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    [Required, Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToPage("/Index");

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        try
        {
            var session = await _supabase.Auth.SignUp(Email, Password);

            if (session?.User == null)
            {
                ErrorMessage = "Registration failed. Please try again.";
                return Page();
            }

            _logger.LogInformation("New user registered: {Email}.", Email);

            // Supabase sends a confirmation email by default.
            // If email confirmation is disabled in your Supabase project, the user is active immediately.
            SuccessMessage = "Registration successful! Check your email to confirm your account.";
            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Registration failed for {Email}.", Email);
            ErrorMessage = "Registration failed. The email may already be in use.";
            return Page();
        }
    }
}
