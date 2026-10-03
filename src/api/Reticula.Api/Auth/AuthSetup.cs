using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Reticula.Domain.Auth;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Identity;

namespace Reticula.Api.Auth;

public static class AuthSetup
{
    public static IServiceCollection AddReticulaAuth(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddAuthentication(IdentityConstants.BearerScheme)
            .AddBearerToken(IdentityConstants.BearerScheme, o =>
            {
                o.BearerTokenExpiration = TimeSpan.FromHours(1);
                // Long refresh window so a tablet can stay signed in through days of offline field work.
                o.RefreshTokenExpiration = TimeSpan.FromDays(14);
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Engineer, p => p.RequireRole(Roles.Engineer))
            .AddPolicy(Policies.FieldUser, p => p.RequireRole(Roles.Engineer, Roles.Inspector));

        services.AddIdentityCore<AppUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 12;
                // Length over composition rules (NIST SP 800-63B).
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireDigit = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireLowercase = false;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ReticulaDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        return services;
    }
}
