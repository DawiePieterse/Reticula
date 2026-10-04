using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reticula.Domain.Auth;

namespace Reticula.Infrastructure.Identity;

public static class IdentitySeeder
{
    /// <summary>Ensures roles exist and, on an empty user table, creates the bootstrap engineer from configuration.</summary>
    public static async Task SeedAsync(IServiceProvider sp, IConfiguration config, ILogger logger, CancellationToken ct = default)
    {
        var roles = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole<Guid>(role));
        }

        var users = sp.GetRequiredService<UserManager<AppUser>>();
        if (await users.Users.AnyAsync(ct)) return;

        var email = config["Bootstrap:EngineerEmail"];
        var password = config["Bootstrap:EngineerPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("No users exist and Bootstrap:EngineerEmail/EngineerPassword are not set. Nobody can log in.");
            return;
        }

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = config["Bootstrap:EngineerName"] ?? email,
            RegistrationNo = config["Bootstrap:EngineerRegistrationNo"],
        };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
            throw new InvalidOperationException("Bootstrap engineer could not be created: " + string.Join("; ", created.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(user, Roles.Engineer);
        logger.LogInformation("Bootstrap engineer {Email} created.", email);
    }
}
