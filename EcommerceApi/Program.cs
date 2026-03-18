using Dapper;
using EcommerceApi.Api.Endpoints;
using EcommerceApi.Application.Orders;
using EcommerceApi.Infrastructure.Repositories;
using EcommerceApi.Infrastructure.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using Serilog;
using Serilog.Formatting.Compact;
using System.Data;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) =>
    cfg.MinimumLevel.Information()
       .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
       .Enrich.FromLogContext()
       .Enrich.WithProperty("Service", "EcommerceApi")
       .WriteTo.Console(new RenderedCompactJsonFormatter()));

// Fail fast if payment secrets are not configured
var devMode = bool.Parse(builder.Configuration["AbacatePay:DevMode"] ?? "false");
if (devMode && builder.Environment.IsProduction())
    throw new InvalidOperationException("DevMode não pode estar habilitado em ambiente de Production.");
var apiToken = devMode
    ? builder.Configuration["AbacatePay:TestApiToken"]
    : builder.Configuration["AbacatePay:ApiToken"];
var webhookSecret = devMode
    ? builder.Configuration["AbacatePay:TestWebhookSecret"]
    : builder.Configuration["AbacatePay:WebhookSecret"];

if (string.IsNullOrWhiteSpace(apiToken))
    throw new InvalidOperationException(devMode
        ? "AbacatePay:TestApiToken não configurado. Use user-secrets ou variável de ambiente."
        : "AbacatePay:ApiToken não configurado. Use user-secrets ou variável de ambiente.");
if (string.IsNullOrWhiteSpace(webhookSecret))
    throw new InvalidOperationException(devMode
        ? "AbacatePay:TestWebhookSecret não configurado. Use user-secrets ou variável de ambiente."
        : "AbacatePay:WebhookSecret não configurado. Use user-secrets ou variável de ambiente.");

// Normalise so the rest of the app always reads the same keys regardless of mode
builder.Configuration["AbacatePay:WebhookSecret"] = webhookSecret;


var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [];
if (!devMode && allowedOrigins.Length == 0)
    throw new InvalidOperationException("AllowedOrigins deve ser configurado em produção. Use fly secrets set.");

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

// Trust X-Forwarded-For from the reverse proxy so per-IP rate limiting uses the real client IP.
// In production, restrict to your actual proxy IP:
//   options.KnownProxies.Add(IPAddress.Parse("10.0.0.1"));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

// Request timeouts — kills hanging connections before they pile up
builder.Services.AddRequestTimeouts(options =>
{
    options.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    // Orders take longer due to file uploads + external API calls
    options.AddPolicy("orders", TimeSpan.FromSeconds(60));
});

// Remove "Server: Kestrel" header + cap request body
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 50 * 1024 * 1024; // 50 MB
});

builder.Services.AddScoped<IDbConnection>(_ => new NpgsqlConnection(connectionString));
builder.Services.AddScoped<ProductRepository>();
builder.Services.AddScoped<StorageService>();
builder.Services.AddScoped<CreateOrderHandler>();
builder.Services.AddScoped<AbacatePayService>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddSingleton<WebhookValidationService>();

builder.Services.AddHttpClient("AbacatePay", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["AbacatePay:ApiBaseUrl"]!);
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiToken}");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(30);
});

var resendToken = builder.Configuration["Resend:ApiToken"]
    ?? throw new InvalidOperationException("Resend:ApiToken não configurado.");

builder.Services.AddHttpClient("Resend", client =>
{
    client.BaseAddress = new Uri("https://api.resend.com");
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {resendToken}");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddHttpClient("R2", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddRateLimiter(opt =>
{
    // Global backstop — 300 req/min per IP across ALL endpoints.
    // Kills abusive clients before any named policy is even evaluated.
    opt.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1)
            }));

    // Products — 120 req/min per IP (public catalogue, generous)
    opt.AddPolicy<string>("api", ctx =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // Orders — relaxed for testing (restore to 5/10min before going live)
    opt.AddPolicy<string>("orders", ctx =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(10),
                SegmentsPerWindow = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // Webhooks — 50 req/min per IP (AbacatePay sends from its own IPs; this handles retries)
    opt.AddPolicy<string>("webhooks", ctx =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 50,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 2
            }));

    // Payment status — 20 req/min per IP (frontend polls after checkout return)
    opt.AddPolicy<string>("paymentStatus", ctx =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    opt.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = 429;
        ctx.HttpContext.Response.Headers["Retry-After"] = "60";
        await ctx.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Too many requests. Tente novamente em breve." }, ct);
    };
});

builder.Services.AddCors(opt =>
    opt.AddPolicy("Frontend", p =>
        p.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [])
         .WithMethods("GET", "POST")
         .AllowAnyHeader()));

var app = builder.Build();

// Must come first — populates real client IP before rate limiting reads it
app.UseForwardedHeaders();

app.UseRequestTimeouts();
app.UseRateLimiter();
app.UseCors("Frontend");

// Security response headers applied to every response
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    // Disable the legacy XSS auditor — it can be weaponized against the client
    ctx.Response.Headers["X-XSS-Protection"] = "0";
    // Prevent browsers/proxies from caching API responses
    ctx.Response.Headers["Cache-Control"] = "no-store";
    await next();
});

var api = app.MapGroup("/api/v1/ecommerce");
api.MapProductEndpoints();
api.MapOrderEndpoints();
api.MapWebhookEndpoints();
api.MapPaymentStatusEndpoints();
api.MapCheckoutSessionEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", ts = DateTime.UtcNow }));

await app.RunAsync();
