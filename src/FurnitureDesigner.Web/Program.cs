using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FurnitureDesigner.Web;
using FurnitureDesigner.Web.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Hosting platforms such as Railway tell the app which port to listen on.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// With APP_PASSWORD set, everyone has to log in with it first (it protects the server's Anthropic API key).
string? password = builder.Configuration["APP_PASSWORD"];
bool passwordGate = !string.IsNullOrEmpty(password);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // Behind the platform's reverse proxy: trust its X-Forwarded-* headers.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/health", () => "ok");

app.MapGet("/login", (string? error) => Results.Content(LoginPage.Html(error is not null), "text/html"));
app.MapPost("/login", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    string given = form["password"].ToString();
    if (!passwordGate || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(password!)))
        return Results.Redirect("/login?error=1");
    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "designer")], CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
    return Results.Redirect("/");
}).DisableAntiforgery();
app.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync();
    return Results.Redirect("/login");
}).DisableAntiforgery();

var components = app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
if (passwordGate)
    components.RequireAuthorization();

app.Run();
