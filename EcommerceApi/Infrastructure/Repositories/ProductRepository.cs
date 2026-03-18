using Dapper;
using System.Data;

namespace EcommerceApi.Infrastructure.Repositories;

public record ProductDto(
    Guid Id,
    string Name,
    string? Description,
    string? Image,
    string? Category,
    string? Size,
    int Price,
    bool ProductStatus,
    string? AbacateStoreProductId
);

public class ProductRepository(IDbConnection db)
{
    public async Task<IEnumerable<ProductDto>> GetActiveAsync() =>
        await db.QueryAsync<ProductDto>(
            """
            SELECT "Id", "Name", "Description", "Image", "Category", "Size", "Price", "ProductStatus", "AbacateStoreProductId"
            FROM products
            WHERE "ProductStatus" = true
            """);

    public async Task<ProductDto?> GetByIdAsync(Guid id) =>
        await db.QueryFirstOrDefaultAsync<ProductDto>(
            """
            SELECT "Id", "Name", "Description", "Image", "Category", "Size", "Price", "ProductStatus", "AbacateStoreProductId"
            FROM products
            WHERE "Id" = @Id
            """,
            new { Id = id });

    public async Task UpdateAbacateProductIdAsync(Guid productId, string abacateProductId) =>
        await db.ExecuteAsync(
            """UPDATE products SET "AbacateStoreProductId" = @AbacateStoreProductId WHERE "Id" = @Id""",
            new { AbacateStoreProductId = abacateProductId, Id = productId });
}
