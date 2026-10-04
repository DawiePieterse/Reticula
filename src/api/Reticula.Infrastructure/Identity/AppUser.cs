using Microsoft.AspNetCore.Identity;

namespace Reticula.Infrastructure.Identity;

public sealed class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";

    /// <summary>ECSA registration number, for the signing engineer.</summary>
    public string? RegistrationNo { get; set; }
}
