using Dapper;
using EcommerceApi.Infrastructure.Services;
using Npgsql;
using NpgsqlTypes;
using System.Data;

namespace EcommerceApi.Application.Orders;

public class CreateOrderHandler(IDbConnection db, StorageService storage, ILogger<CreateOrderHandler> logger)
{
    public async Task<CreateOrderResult> HandleAsync(CreateOrderCommand command)
    {
        Validate(command);

        var orderId = Guid.CreateVersion7();

        // 1. Upload all reference images to R2 before opening the DB transaction
        var allReferenceKeys = new List<string>();
        for (int i = 0; i < command.Items.Count; i++)
        {
            var item = command.Items[i];
            for (int j = 0; j < item.References.Count; j++)
            {
                var file = item.References[j];
                var ext = Path.GetExtension(file.FileName).TrimStart('.');
                if (string.IsNullOrEmpty(ext)) ext = "jpg";
                var key = $"ecommerce/orders/{orderId}/ref_{i}_{j}.{ext}";
                await storage.UploadFileAsync(file.Data, key, file.ContentType);
                allReferenceKeys.Add(key);
            }
        }

        // 2. Open connection and start transaction
        var conn = (NpgsqlConnection)db;
        conn.Open();
        using var tx = conn.BeginTransaction();

        try
        {
            // 3. Find or create client
            var clientId = await conn.QueryFirstOrDefaultAsync<Guid?>(
                """SELECT "Id" FROM clients WHERE "Name" = @Name AND "Mobile" = @Mobile LIMIT 1""",
                new { Name = command.ClientName, Mobile = command.ClientMobile },
                transaction: tx);

            if (clientId is null)
            {
                clientId = Guid.NewGuid();
                await conn.ExecuteAsync(
                    """
                    INSERT INTO clients ("Id", "Name", "Mobile", "Status", "CreatedAt")
                    VALUES (@Id, @Name, @Mobile, true, @CreatedAt)
                    """,
                    new { Id = clientId, Name = command.ClientName, Mobile = command.ClientMobile, CreatedAt = DateTime.UtcNow },
                    transaction: tx);

                logger.LogInformation("New client created: {ClientId}", clientId);
            }

            // 4. Validate products and calculate totals
            long orderTotalPaid = 0;
            long orderTotalValue = 0;
            var now = DateTime.UtcNow;

            var itemsToInsert = new List<(Guid ProductId, int Quantity, int PaidUnitPrice, int BaseUnitPrice, long TotalPaid, long TotalValue, string? Observation)>();

            foreach (var item in command.Items)
            {
                var product = await conn.QueryFirstOrDefaultAsync<(Guid Id, int Price)>(
                    """SELECT "Id", "Price" FROM products WHERE "Id" = @Id AND "ProductStatus" = true""",
                    new { Id = item.ProductId },
                    transaction: tx);

                if (product == default)
                    throw new InvalidOperationException($"Produto {item.ProductId} não encontrado ou inativo.");

                var itemTotalPaid = (long)item.Quantity * item.PaidPrice;
                var itemTotalValue = (long)item.Quantity * product.Price;
                orderTotalPaid += itemTotalPaid;
                orderTotalValue += itemTotalValue;

                itemsToInsert.Add((item.ProductId, item.Quantity, item.PaidPrice, product.Price, itemTotalPaid, itemTotalValue, item.Observation));
            }

            // 5. Insert order — use NpgsqlCommand directly to handle text[] References
            using (var cmd = new NpgsqlCommand(
                """
                INSERT INTO orders ("Id", "ClientId", "DeliveryDate", "Status", "TotalPaid", "TotalValue", "References", "CreatedAt", "UpdatedAt")
                VALUES (@Id, @ClientId, @DeliveryDate, @Status, @TotalPaid, @TotalValue, @References, @CreatedAt, @UpdatedAt)
                """,
                conn, tx))
            {
                cmd.Parameters.AddWithValue("Id", orderId);
                cmd.Parameters.AddWithValue("ClientId", clientId.Value);
                cmd.Parameters.AddWithValue("DeliveryDate", command.DeliveryDate);
                cmd.Parameters.AddWithValue("Status", 1); // Pending
                cmd.Parameters.AddWithValue("TotalPaid", orderTotalPaid);
                cmd.Parameters.AddWithValue("TotalValue", orderTotalValue);
                cmd.Parameters.AddWithValue("CreatedAt", now);
                cmd.Parameters.AddWithValue("UpdatedAt", now);

                var refsParam = new NpgsqlParameter("References", NpgsqlDbType.Array | NpgsqlDbType.Text);
                refsParam.Value = allReferenceKeys.Count > 0 ? allReferenceKeys.ToArray() : DBNull.Value;
                cmd.Parameters.Add(refsParam);

                await cmd.ExecuteNonQueryAsync();
            }

            // 6. Insert order items
            foreach (var (productId, quantity, paidUnitPrice, baseUnitPrice, totalPaid, totalValue, observation) in itemsToInsert)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO order_items ("OrderId", "ProductId", "Quantity", "PaidUnitPrice", "BaseUnitPrice", "TotalPaid", "TotalValue", "Observation", "ItemCanceled", "CreatedAt", "UpdatedAt")
                    VALUES (@OrderId, @ProductId, @Quantity, @PaidUnitPrice, @BaseUnitPrice, @TotalPaid, @TotalValue, @Observation, false, @CreatedAt, @UpdatedAt)
                    """,
                    new { OrderId = orderId, ProductId = productId, Quantity = quantity, PaidUnitPrice = paidUnitPrice, BaseUnitPrice = baseUnitPrice, TotalPaid = totalPaid, TotalValue = totalValue, Observation = observation, CreatedAt = now, UpdatedAt = now },
                    transaction: tx);
            }

            tx.Commit();

            logger.LogInformation("Order {OrderId} created for client {ClientId} — total {TotalPaid}", orderId, clientId, orderTotalPaid);

            return new CreateOrderResult(orderId, clientId.Value, orderTotalPaid);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static void Validate(CreateOrderCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.ClientName))
            throw new ArgumentException("clientName é obrigatório.");
        if (string.IsNullOrWhiteSpace(cmd.ClientMobile))
            throw new ArgumentException("clientMobile é obrigatório.");
        if (cmd.DeliveryDate == default)
            throw new ArgumentException("deliveryDate inválido.");
        if (cmd.Items.Count == 0)
            throw new ArgumentException("O pedido deve ter pelo menos 1 item.");

        foreach (var item in cmd.Items)
        {
            if (item.Quantity <= 0)
                throw new ArgumentException($"Quantidade inválida para o produto {item.ProductId}.");
            if (item.PaidPrice < 0)
                throw new ArgumentException($"Preço inválido para o produto {item.ProductId}.");
        }
    }
}
