using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Reticula.Infrastructure.Identity;

namespace Reticula.Api.Auth;

public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record MeResponse(Guid Id, string Email, string DisplayName, string? RegistrationNo, IReadOnlyList<string> Roles);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");

        g.MapPost("/login", Login).AllowAnonymous().Produces<AccessTokenResponse>();
        g.MapPost("/refresh", Refresh).AllowAnonymous().Produces<AccessTokenResponse>();
        g.MapGet("/me", Me).RequireAuthorization();

        return app;
    }

    private static async Task<Results<EmptyHttpResult, ProblemHttpResult>> Login(
        LoginRequest req, SignInManager<AppUser> signIn, UserManager<AppUser> users)
    {
        var user = string.IsNullOrWhiteSpace(req.Email) ? null : await users.FindByEmailAsync(req.Email);
        if (user is null)
            return TypedResults.Problem("Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);

        // Bearer scheme: SignInManager writes the access/refresh token response itself.
        signIn.AuthenticationScheme = IdentityConstants.BearerScheme;
        var result = await signIn.PasswordSignInAsync(user, req.Password ?? "", isPersistent: false, lockoutOnFailure: true);
        if (result.IsLockedOut)
            return TypedResults.Problem("Account locked after repeated failed attempts. Try again later.", statusCode: StatusCodes.Status401Unauthorized);
        if (!result.Succeeded)
            return TypedResults.Problem("Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);

        return TypedResults.Empty;
    }

    private static async Task<Results<SignInHttpResult, ChallengeHttpResult>> Refresh(
        RefreshRequest req, SignInManager<AppUser> signIn, IOptionsMonitor<BearerTokenOptions> options, TimeProvider time)
    {
        var protector = options.Get(IdentityConstants.BearerScheme).RefreshTokenProtector;
        var ticket = string.IsNullOrEmpty(req.RefreshToken) ? null : protector.Unprotect(req.RefreshToken);

        if (ticket?.Properties.ExpiresUtc is not { } expires
            || time.GetUtcNow() >= expires
            || await signIn.ValidateSecurityStampAsync(ticket.Principal) is not { } user)
        {
            return TypedResults.Challenge();
        }

        var principal = await signIn.CreateUserPrincipalAsync(user);
        return TypedResults.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
    }

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> Me(ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return TypedResults.Unauthorized();
        var roles = await users.GetRolesAsync(user);
        return TypedResults.Ok(new MeResponse(user.Id, user.Email ?? "", user.DisplayName, user.RegistrationNo, [.. roles]));
    }
}
