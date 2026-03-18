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
var apiToken = builder.Configuration["AbacatePay:ApiToken"];
var webhookSecret = builder.Configuration["AbacatePay:WebhookSecret"];
if (string.IsNullOrWhiteSpace(apiToken))
    throw new InvalidOperationException(
        "AbacatePay:ApiToken não configurado. Use user-secrets ou variável de ambiente.");
if (string.IsNullOrWhiteSpace(webhookSecret))
    throw new InvalidOperationException(
        "AbacatePay:WebhookSecret não configurado. Use user-secrets ou variável de ambiente.");

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

builder.Services.AddTransient<IDbConnection>(_ => new NpgsqlConnection(connectionString));
builder.Services.AddScoped<ProductRepository>();
builder.Services.AddScoped<StorageService>();
builder.Services.AddScoped<CreateOrderHandler>();
builder.Services.AddScoped<AbacatePayService>();
builder.Services.AddSingleton<WebhookValidationService>();

builder.Services.AddHttpClient("AbacatePay", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["AbacatePay:ApiBaseUrl"]!);
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiToken}");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
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

    // Orders — 5 per 10 min per IP (strict — one IP creating many orders is brute force)
    opt.AddPolicy<string>("orders", ctx =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 5,
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

app.MapGet("/health", () => Results.Ok(new { status = "healthy", ts = DateTime.UtcNow }));

await app.RunAsync();
