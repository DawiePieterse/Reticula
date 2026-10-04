using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Auth;
using Reticula.Infrastructure.Identity;

namespace Reticula.Api.Auth;

public sealed record UserDto(Guid Id, string Email, string DisplayName, string? RegistrationNo, IReadOnlyList<string> Roles);
public sealed record CreateUserRequest(string Email, string DisplayName, string Password, string Role, string? RegistrationNo);

/// <summary>User management. Engineer only: there is no self-registration.</summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization(Policies.Engineer);
        g.MapGet("/", List);
        g.MapPost("/", Create);
        return app;
    }

    private static async Task<Ok<List<UserDto>>> List(UserManager<AppUser> users, CancellationToken ct)
    {
        var all = await users.Users.OrderBy(u => u.Email).ToListAsync(ct);
        var result = new List<UserDto>(all.Count);
        foreach (var u in all)
            result.Add(new UserDto(u.Id, u.Email ?? "", u.DisplayName, u.RegistrationNo, [.. await users.GetRolesAsync(u)]));
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Created<UserDto>, ValidationProblem>> Create(CreateUserRequest req, UserManager<AppUser> users)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(req.Email)) errors["email"] = ["Email is required."];
        if (string.IsNullOrWhiteSpace(req.DisplayName)) errors["displayName"] = ["Display name is required."];
        if (!Roles.All.Contains(req.Role)) errors["role"] = [$"Role must be one of: {string.Join(", ", Roles.All)}."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var user = new AppUser
        {
            UserName = req.Email,
            Email = req.Email,
            EmailConfirmed = true,
            DisplayName = req.DisplayName.Trim(),
            RegistrationNo = string.IsNullOrWhiteSpace(req.RegistrationNo) ? null : req.RegistrationNo.Trim(),
        };
        var created = await users.CreateAsync(user, req.Password ?? "");
        if (!created.Succeeded)
            return TypedResults.ValidationProblem(created.Errors.GroupBy(e => e.Code.Contains("Password") ? "password" : "email")
                .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));

        await users.AddToRoleAsync(user, req.Role);
        return TypedResults.Created($"/api/users/{user.Id}", new UserDto(user.Id, user.Email, user.DisplayName, user.RegistrationNo, [req.Role]));
    }
}
