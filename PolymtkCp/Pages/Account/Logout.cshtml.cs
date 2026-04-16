using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PolymtkCp.Pages.Account;

public class LogoutModel : PageModel
{
    private readonly Supabase.Client _supabase;
    private readonly ILogger<LogoutModel> _logger;

    public LogoutModel(Supabase.Client supabase, ILogger<LogoutModel> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;

        await _supabase.Auth.SignOut();
        await HttpContext.SignOutAsync("Cookies");

        _logger.LogInformation("User {Email} logged out.", email);

        return RedirectToPage("/Index");
    }
}
