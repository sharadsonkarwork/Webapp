using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Rosterix.Api.Auth;
using Rosterix.Application.Ai;
using Rosterix.Application.Allowance;
using Rosterix.Application.Data;
using Rosterix.Application.Security;
using Rosterix.Domain.Rules;
using Rosterix.Infrastructure.Persistence;
using Rosterix.Infrastructure.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter()).WriteTo.File("logs/rosterix-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14));

var cfg = builder.Configuration;
var inMemory = cfg.GetValue<bool>("UseInMemory");
var isDev = builder.Environment.IsDevelopment();

builder.Services.AddDbContext<RosterixDbContext>(o =>
{
    if (inMemory) o.UseInMemoryDatabase(cfg["InMemoryName"] ?? "rosterix");
    else o.UseSqlServer(cfg.GetConnectionString("Rosterix") ?? throw new InvalidOperationException("ConnectionStrings:Rosterix is not set (use an environment variable or user-secrets)."), s => s.EnableRetryOnFailure());
    o.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning));
});
DateOnly? fixedToday = DateOnly.TryParse(cfg["Rosterix:FixedToday"], out var ft) ? ft : null;
builder.Services.AddSingleton<IClock>(new Rosterix.Application.Data.SystemClock(fixedToday));
builder.Services.AddSingleton<IRuleEngine, RuleEngine>();
builder.Services.AddSingleton<AllowanceCalculator>();
builder.Services.AddSingleton<IAiProvider>(cfg.GetValue<bool>("Ai:LlmNarration") ? new LlmNarrationProvider(true) : new HeuristicAiProvider());
builder.Services.AddScoped<IAiDataSource, EfAiDataSource>();
builder.Services.AddScoped<ProjectSetupAi>().AddScoped<EmployeeTaggingAi>().AddScoped<RosterGenerationAi>().AddScoped<ConstraintValidationAi>()
    .AddScoped<CostPredictionAi>().AddScoped<MyAllowanceExplainer>().AddScoped<ConflictRippleAi>().AddScoped<ApprovalRiskAi>().AddScoped<PreferenceLearningAi>().AddScoped<AnomalyDetectionAi>();
builder.Services.AddScoped<IAuditSink, DbAuditSink>().AddScoped<INotifier, Notifier>().AddScoped<ApprovalService>().AddScoped<DbSeeder>();
builder.Services.AddSingleton<ISecretProvider, EnvSecretProvider>();
builder.Services.AddHttpContextAccessor().AddScoped<ICurrentUser, CurrentUser>();

// ---- AuthN: DevAuth only in Development (SEC-08); Azure AD OIDC (JWT bearer from auth-code + PKCE SPA) elsewhere.
var auth = builder.Services.AddAuthentication(isDev ? DevAuthHandler.Scheme : JwtBearerDefaults.AuthenticationScheme);
if (isDev) auth.AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.Scheme, _ => { });
else
{
    auth.AddJwtBearer(o =>
    {
        o.Authority = $"https://login.microsoftonline.com/{cfg["AzureAd:TenantId"]}/v2.0";
        o.TokenValidationParameters.ValidAudience = cfg["AzureAd:ClientId"];
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async c =>
            {
                var db = c.HttpContext.RequestServices.GetRequiredService<RosterixDbContext>();
                var upn = c.Principal?.FindFirst("preferred_username")?.Value ?? c.Principal?.FindFirst("upn")?.Value ?? c.Principal?.FindFirst("email")?.Value;
                var emp = upn == null ? null : await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Email == upn && e.IsActive);
                if (emp == null) { c.Fail("No active Rosterix employee matches this sign-in."); return; }
                c.Principal = ClaimsFactory.For(emp, JwtBearerDefaults.AuthenticationScheme);
            }
        };
    });
}
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    foreach (var (name, roles) in Policies.Map)
        o.AddPolicy(name, p => p.RequireAuthenticatedUser().RequireRole(Policies.Split(roles).Select(r => r.ToString())));
});

builder.Services.AddControllers(o => o.Filters.Add<AuditActionFilter>())
    .AddJsonOptions(o => { o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()); o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles; });
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(cfg.GetSection("Cors:Origins").Get<string[]>() ?? new[] { "http://localhost:4200" }).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition")));
builder.Services.AddEndpointsApiExplorer().AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Rosterix 2.0 API", Version = "v1", Description = "Workforce, roster and allowance management. AI endpoints under /api/ai are advisory and read-only; nothing they return is persisted without an explicit human action." });
    o.AddSecurityDefinition("DevPersona", new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Dev-Persona", Description = "Development only: priya | arjun | meera | karthik | fathima | ravi" });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement { [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "DevPersona" } }] = new List<string>() });
});
builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); o.IncludeSubDomains = true; });
builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13)); // SEC-05
builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();
app.UseForwardedHeaders();
if (!isDev) { app.UseHsts(); app.UseHttpsRedirection(); }                   // SEC-05
app.Use(async (c, n) => { c.Response.Headers["X-Content-Type-Options"] = "nosniff"; c.Response.Headers["Referrer-Policy"] = "no-referrer"; c.Response.Headers["Cache-Control"] = c.Request.Path.StartsWithSegments("/api") ? "no-store" : c.Response.Headers["Cache-Control"]; await n(); });
app.UseSerilogRequestLogging();
app.UseExceptionHandler(a => a.Run(async c =>
{
    var ex = c.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    c.Response.ContentType = "application/problem+json";
    if (ex is ApprovalException ae) { c.Response.StatusCode = ae.Status; await c.Response.WriteAsJsonAsync(new { title = ae.Message, status = ae.Status }); return; }
    Log.Error(ex, "Unhandled error");
    c.Response.StatusCode = 500; await c.Response.WriteAsJsonAsync(new { title = "Something went wrong on our side. Try again; if it persists, quote the time of the error to support.", status = 500 });
}));
app.UseCors();
app.UseSwagger(); app.UseSwaggerUI();
app.UseDefaultFiles(); app.UseStaticFiles();
app.UseAuthentication();
app.UseMiddleware<AuditDeniedMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html"))) app.MapFallbackToFile("index.html").AllowAnonymous();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<DbSeeder>().MigrateAndSeedAsync(inMemory);

app.Run();
public partial class Program { }
