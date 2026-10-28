using FluentMigrator.Runner;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.ResponseCompression;
using SpoolManager.Infrastructure;
using SpoolManager.Infrastructure.Data;
using SpoolManager.Infrastructure.Migrations;
using SpoolManager.Infrastructure.Repositories;
using SpoolManager.Infrastructure.Services;
using SpoolManager.Server.Filters;
using SpoolManager.Server.Services;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")!;

builder.Services.AddSpoolManagerDb(connectionString);

builder.Services.AddFluentMigratorCore()
    .ConfigureRunner(r => r
        .AddMySql8()
        .WithGlobalConnectionString(connectionString)
        .ScanIn(typeof(M001_InitialSchema).Assembly).For.Migrations())
    .AddLogging(lb => lb.AddFluentMigratorConsole());

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IMaterialRepository, MaterialRepository>();
builder.Services.AddScoped<ISpoolRepository, SpoolRepository>();
builder.Services.AddScoped<IOwnerRepository, OwnerRepository>();
builder.Services.AddScoped<IPrinterRepository, PrinterRepository>();
builder.Services.AddScoped<IStorageLocationRepository, StorageLocationRepository>();
builder.Services.AddScoped<IDryerRepository, DryerRepository>();
builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<IProjectMemberRepository, ProjectMemberRepository>();
builder.Services.AddScoped<IInvitationRepository, InvitationRepository>();
builder.Services.AddScoped<IOpenSpoolService, OpenSpoolService>();
builder.Services.AddScoped<IImageService, ImageService>();
builder.Services.AddScoped<IMaterialExportService, MaterialExportService>();
builder.Services.AddScoped<IOrcaExportService, OrcaExportService>();
builder.Services.AddScoped<ISuggestionRepository, SuggestionRepository>();
builder.Services.AddScoped<IFavoriteRepository, FavoriteRepository>();
builder.Services.AddHttpClient<IOfdSyncService, OfdSyncService>();
builder.Services.AddScoped<IAuditLogRepository, AuditLogRepository>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<ITicketRepository, TicketRepository>();
builder.Services.AddScoped<ISmtpSettingsRepository, SmtpSettingsRepository>();
builder.Services.AddScoped<ISiteSettingsRepository, SiteSettingsRepository>();
builder.Services.AddScoped<IMaterialCatalogSettings, MaterialCatalogSettings>();
builder.Services.AddScoped<IBackupService, BackupService>();
builder.Services.AddScoped<SpoolmanService>();
builder.Services.AddSingleton<SpoolmanConnections>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddHostedService<NotificationBackgroundService>();

builder.Services.AddScoped<ProjectAuthFilter>();

builder.Services.AddAuthentication("LocalInventory")
    .AddScheme<AuthenticationSchemeOptions, LocalInventoryAuthenticationHandler>("LocalInventory", _ => { });
// Initialized after migrations, before requests can be served.
LocalInventoryIdentity? sharedInventory = null;
builder.Services.AddSingleton(_ => sharedInventory!);

builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddHttpClient("updater", client => client.Timeout = TimeSpan.FromSeconds(8));

builder.Services.AddResponseCompression(opts =>
    opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/octet-stream"]));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
    runner.MigrateUp();

    sharedInventory = await LocalInventoryIdentity.InitializeAsync(
        scope.ServiceProvider.GetRequiredService<SpoolManagerDb>(),
        scope.ServiceProvider.GetRequiredService<ISiteSettingsRepository>());
}

app.UseResponseCompression();
app.UseBlazorFrameworkFiles();

app.MapGet("/favicon.ico", async (ISiteSettingsRepository settings, HttpContext ctx) =>
{
    var data = await settings.GetAsync("branding_favicon");
    if (string.IsNullOrEmpty(data) || !data.Contains(','))
    {
        ctx.Response.Redirect("/favicon.png");
        return;
    }
    var semiIdx = data.IndexOf(';');
    var commaIdx = data.IndexOf(',');
    var mime = semiIdx > 5 ? data[5..semiIdx] : "image/x-icon";
    var bytes = Convert.FromBase64String(data[(commaIdx + 1)..]);
    ctx.Response.Headers.CacheControl = "public, max-age=3600";
    ctx.Response.ContentType = mime;
    await ctx.Response.Body.WriteAsync(bytes);
});

app.UseStaticFiles();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10), KeepAliveTimeout = TimeSpan.FromSeconds(10) });
// Old bookmarked account URLs lead directly to the inventory.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.TrimEnd('/').ToLowerInvariant() ?? "";
    if (HttpMethods.IsGet(context.Request.Method) && new[] { "/login", "/register", "/forgot-password", "/reset-password", "/verify-email", "/settings/account", "/settings/password", "/admin/users" }.Contains(path))
    { context.Response.Redirect("/"); return; }
    // Prevent unrelated websites from issuing browser mutations against this LAN app.
    if (context.Request.Path.StartsWithSegments("/api") &&
        ((!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method)) || context.WebSockets.IsWebSocketRequest) &&
        context.Request.Headers.TryGetValue("Origin", out var origin) &&
        (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri) || !string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
    { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { message = "Cross-origin changes are not allowed." }); return; }
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
// A misspelled compatibility URL must never return the application HTML.
app.Map("/api/spoolman/{**path}", async (HttpContext ctx) =>
{
    ctx.Response.StatusCode = 404;
    await ctx.Response.WriteAsJsonAsync(new { message = "Not found." });
});

var webRoot = app.Environment.WebRootPath
    ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var indexHtmlPath = Path.Combine(webRoot, "index.html");
if (!File.Exists(indexHtmlPath))
{
    var clientWwwroot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath,
        "..", "SpoolManager.Client", "wwwroot", "index.html"));
    if (File.Exists(clientWwwroot))
        indexHtmlPath = clientWwwroot;
}
app.MapFallback(async (HttpContext ctx) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { message = "Not found." }); return; }
    if (!File.Exists(indexHtmlPath))
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    var html = await File.ReadAllTextAsync(indexHtmlPath);
    var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    html = html.Replace("__ORIGIN__", origin);
    ctx.Response.ContentType = "text/html; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";
    await ctx.Response.WriteAsync(html);
});

app.Run();
