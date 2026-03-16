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
    bool ProductStatus
);

public class ProductRepository(IDbConnection db)
{
    public async Task<IEnumerable<ProductDto>> GetActiveAsync() =>
        await db.QueryAsync<ProductDto>(
            """
            SELECT "Id", "Name", "Description", "Image", "Category", "Size", "Price", "ProductStatus"
            FROM products
            WHERE "ProductStatus" = true
            """);

    public async Task<ProductDto?> GetByIdAsync(Guid id) =>
        await db.QueryFirstOrDefaultAsync<ProductDto>(
            """
            SELECT "Id", "Name", "Description", "Image", "Category", "Size", "Price", "ProductStatus"
            FROM products
            WHERE "Id" = @Id 
            """,
            new { Id = id });
}
