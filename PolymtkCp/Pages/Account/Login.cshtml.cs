using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PolymtkCp.Pages.Account;

public class LoginModel : PageModel
{
    private readonly Supabase.Client _supabase;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(Supabase.Client supabase, ILogger<LoginModel> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    [BindProperty]
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    public string Password { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToPage("/Index");

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (!ModelState.IsValid)
            return Page();

        try
        {
            var session = await _supabase.Auth.SignIn(Email, Password);

            if (session?.User == null)
            {
                ErrorMessage = "Invalid email or password.";
                return Page();
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, session.User.Id ?? string.Empty),
                new(ClaimTypes.Email, session.User.Email ?? string.Empty),
            };

            var identity = new ClaimsIdentity(claims, "Cookies");
            var principal = new ClaimsPrincipal(identity);

            await HttpContext.SignInAsync("Cookies", principal);

            _logger.LogInformation("User {Email} logged in.", Email);

            return LocalRedirect(returnUrl ?? "/");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Login failed for {Email}.", Email);
            ErrorMessage = "Invalid email or password.";
            return Page();
        }
    }
}
