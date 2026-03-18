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
    string? AbacateStoreProductId,
    int? AbacateStoreProductPrice
);

public class ProductRepository(IDbConnection db)
{
    public async Task<IEnumerable<ProductDto>> GetActiveAsync() =>
        await db.QueryAsync<ProductDto>(
            """
            SELECT "Id", "Name", "Description", "Image", "Category", "Size", "Price", "ProductStatus", "AbacateStoreProductId", "AbacateStoreProductPrice"
            FROM products
            WHERE "ProductStatus" = true
            """);

    public async Task<ProductDto?> GetByIdAsync(Guid id) =>
        await db.QueryFirstOrDefaultAsync<ProductDto>(
            """
            SELECT "Id", "Name", "Description", "Image", "Category", "Size", "Price", "ProductStatus", "AbacateStoreProductId", "AbacateStoreProductPrice"
            FROM products
            WHERE "Id" = @Id
            """,
            new { Id = id });

    public async Task UpdateAbacateProductAsync(Guid productId, string abacateProductId, int cardPrice) =>
        await db.ExecuteAsync(
            """
            UPDATE products
            SET "AbacateStoreProductId" = @AbacateStoreProductId,
                "AbacateStoreProductPrice" = @AbacateStoreProductPrice
            WHERE "Id" = @Id
            """,
            new { AbacateStoreProductId = abacateProductId, AbacateStoreProductPrice = cardPrice, Id = productId });
}
