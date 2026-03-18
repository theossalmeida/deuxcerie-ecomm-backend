using EcommerceApi.Infrastructure.Repositories;

namespace EcommerceApi.Api.Endpoints;

public static class ProductEndpoints
{
    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/products", async (ProductRepository repo) =>
        {
            var products = await repo.GetActiveAsync();
            return Results.Ok(products.Select(ToResponse));
        })
        .RequireRateLimiting("api");

        app.MapGet("/products/{id:guid}", async (Guid id, ProductRepository repo) =>
        {
            var product = await repo.GetByIdAsync(id);
            return product is null ? Results.NotFound() : Results.Ok(ToResponse(product));
        })
        .RequireRateLimiting("api");

        return app;
    }

    private static object ToResponse(ProductDto p) => new
    {
        p.Id,
        p.Name,
        p.Description,
        p.Image,
        p.Category,
        p.Size,
        p.ProductStatus,
        PixPrice  = p.Price,
        CardPrice = (int)Math.Round(p.Price * 1.05),
    };
}
