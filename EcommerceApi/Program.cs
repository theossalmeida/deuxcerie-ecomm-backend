using Dapper;
using EcommerceApi.Api.Endpoints;
using EcommerceApi.Infrastructure.Repositories;
using Npgsql;
using Serilog;
using Serilog.Formatting.Compact;
using System.Data;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) =>
    cfg.MinimumLevel.Information()
       .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
       .Enrich.FromLogContext()
       .Enrich.WithProperty("Service", "EcommerceApi")
       .WriteTo.Console(new RenderedCompactJsonFormatter()));

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

builder.Services.AddTransient<IDbConnection>(_ => new NpgsqlConnection(connectionString));

builder.Services.AddScoped<ProductRepository>();

builder.Services.AddRateLimiter(opt =>
{
    opt.AddFixedWindowLimiter("api", lim =>
    {
        lim.PermitLimit = 60;
        lim.Window = TimeSpan.FromMinutes(1);
    });
    opt.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = 429;
        await ctx.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many requests" }, ct);
    };
});

builder.Services.AddCors(opt =>
    opt.AddPolicy("Frontend", p =>
        p.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [])
         .WithMethods("GET", "POST")
         .AllowAnyHeader()));

var app = builder.Build();

app.UseRateLimiter();
app.UseCors("Frontend");

var api = app.MapGroup("/api/v1/ecommerce");
api.MapProductEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", ts = DateTime.UtcNow }));

await app.RunAsync();
