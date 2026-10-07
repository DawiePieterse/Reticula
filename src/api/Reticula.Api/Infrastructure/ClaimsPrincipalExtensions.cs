using System.Security.Claims;

namespace Reticula.Api.Infrastructure;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The signed-in user's id. Every endpoint using it sits behind authorization, so the claim is always present.</summary>
    public static Guid UserId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
