using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Reticula.Api.Auth;
using Reticula.Domain.Auth;

namespace Reticula.Api.Tests;

public class AuthTests(ReticulaApiFactory factory) : IClassFixture<ReticulaApiFactory>
{
    [Fact]
    public async Task Bootstrap_engineer_can_log_in_and_read_profile()
    {
        var client = await factory.EngineerClientAsync();
        var me = await client.GetFromJsonAsync<MeResponse>("/api/auth/me");
        Assert.Equal(ReticulaApiFactory.EngineerEmail, me!.Email);
        Assert.Equal([Roles.Engineer], me.Roles);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var r = await factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = ReticulaApiFactory.EngineerEmail, password = "wrong-password-here" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Unknown_user_is_rejected()
    {
        var r = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "nobody@test.local", password = "whatever-123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Refresh_token_issues_new_access_token()
    {
        var client = factory.CreateClient();
        var tokens = await ReticulaApiFactory.LoginAsync(client, ReticulaApiFactory.EngineerEmail, ReticulaApiFactory.EngineerPassword);

        var r = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var refreshed = await r.Content.ReadFromJsonAsync<AccessTokenResponse>();
        Assert.False(string.IsNullOrEmpty(refreshed!.AccessToken));
    }

    [Fact]
    public async Task Garbage_refresh_token_is_rejected()
    {
        var r = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = "not-a-token" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Anonymous_requests_get_401()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Engineer_creates_inspector_who_cannot_manage_users()
    {
        var engineer = await factory.EngineerClientAsync();
        var email = $"inspector-{Guid.NewGuid():N}@test.local";
        var created = await engineer.PostAsJsonAsync("/api/users",
            new CreateUserRequest(email, "Field Inspector", "inspector-pass-1", Roles.Inspector, null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var inspector = await factory.ClientAsAsync(email, "inspector-pass-1");
        var me = await inspector.GetFromJsonAsync<MeResponse>("/api/auth/me");
        Assert.Equal([Roles.Inspector], me!.Roles);

        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.GetAsync("/api/users")).StatusCode);
        var r = await inspector.PostAsJsonAsync("/api/users", new CreateUserRequest($"x{email}", "X", "inspector-pass-1", Roles.Engineer, null));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Create_user_validates_role_and_password()
    {
        var engineer = await factory.EngineerClientAsync();
        var badRole = await engineer.PostAsJsonAsync("/api/users", new CreateUserRequest("a@test.local", "A", "long-enough-pass", "admin", null));
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);

        var shortPw = await engineer.PostAsJsonAsync("/api/users", new CreateUserRequest("b@test.local", "B", "short", Roles.Inspector, null));
        Assert.Equal(HttpStatusCode.BadRequest, shortPw.StatusCode);
    }
}
