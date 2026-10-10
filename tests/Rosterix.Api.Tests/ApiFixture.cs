using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rosterix.Infrastructure.Persistence;

namespace Rosterix.Api.Tests;

public class ApiFixture : WebApplicationFactory<Program>
{
    public string Db { get; } = "rx-" + Guid.NewGuid();
    public string Env { get; init; } = "Development";
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Env);
        foreach (var (k, v) in new Dictionary<string, string> { ["UseInMemory"] = "true", ["InMemoryName"] = Db, ["Rosterix:FixedToday"] = "2026-10-10", ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000000", ["AzureAd:ClientId"] = "test" })
            builder.UseSetting(k, v); // minimal hosting reads config eagerly, so settings must be host settings
    }
    public HttpClient As(string persona) { var c = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }); c.DefaultRequestHeaders.Add("X-Dev-Persona", persona); return c; }
    public async Task<T> Scope<T>(Func<RosterixDbContext, Task<T>> f) { using var s = Services.CreateScope(); return await f(s.ServiceProvider.GetRequiredService<RosterixDbContext>()); }
}

public static class Http
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    public static async Task<JsonNode> J(this HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync()) ?? throw new InvalidOperationException("empty body");
    public static Task<HttpResponseMessage> Post(this HttpClient c, string url, object? body = null) => c.PostAsJsonAsync(url, body ?? new { }, Json);
    public static Task<HttpResponseMessage> Put(this HttpClient c, string url, object? body = null) => c.PutAsJsonAsync(url, body ?? new { }, Json);
    public static async Task<JsonNode> GetJ(this HttpClient c, string url) { var r = await c.GetAsync(url); r.EnsureSuccessStatusCode(); return await r.J(); }
}
