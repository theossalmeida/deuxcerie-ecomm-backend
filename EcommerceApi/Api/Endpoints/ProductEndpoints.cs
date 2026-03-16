using EcommerceApi.Infrastructure.Repositories;

namespace EcommerceApi.Api.Endpoints;

public static class ProductEndpoints
{
    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/products", async (ProductRepository repo) =>
        {
            var products = await repo.GetActiveAsync();
            return Results.Ok(products);
        })
        .RequireRateLimiting("api");

        app.MapGet("/products/{id:guid}", async (Guid id, ProductRepository repo) =>
        {
            var product = await repo.GetByIdAsync(id);
            return product is null ? Results.NotFound() : Results.Ok(product);
        })
        .RequireRateLimiting("api");

        return app;
    }
}
