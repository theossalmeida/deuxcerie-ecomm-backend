using Dapper;
using EcommerceApi.Infrastructure.DTOs;
using EcommerceApi.Infrastructure.Services;
using System.Data;
using System.Text.Json;

namespace EcommerceApi.Application.Orders;

public class PaymentGatewayException(string message) : Exception(message);

public class CreateOrderHandler(
    IDbConnection db,
    StorageService storage,
    AbacatePayService abacatePay,
    IConfiguration config,
    ILogger<CreateOrderHandler> logger)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<CreateOrderResult> HandleAsync(CreateOrderCommand command)
    {
        Validate(command);

        var sessionId = Guid.CreateVersion7();

        // 1. Upload all reference images to R2 before any external calls
        var allReferenceKeys = new List<string>();
        for (int i = 0; i < command.Items.Count; i++)
        {
            var item = command.Items[i];
            for (int j = 0; j < item.References.Count; j++)
            {
                var file = item.References[j];
                var ext = Path.GetExtension(file.FileName).TrimStart('.');
                if (string.IsNullOrEmpty(ext)) ext = "jpg";
                var key = $"ecommerce/orders/{sessionId}/ref_{i}_{j}.{ext}";
                await storage.UploadFileAsync(file.Data, key, file.ContentType);
                allReferenceKeys.Add(key);
            }
        }

        // 2. Validate products and calculate totals (critical security check — before charging)
        long amountCents = 0;
        var itemSnapshots = new List<CheckoutItemSnapshot>();

        foreach (var item in command.Items)
        {
            var product = await db.QueryFirstOrDefaultAsync<(Guid Id, int Price, string Name)>(
                """SELECT "Id", "Price", "Name" FROM products WHERE "Id" = @Id AND "ProductStatus" = true""",
                new { Id = item.ProductId });

            if (product == default)
                throw new InvalidOperationException($"Produto {item.ProductId} não encontrado ou inativo.");

            if (item.PaidPrice < product.Price)
                throw new InvalidOperationException(
                    $"O preço do produto {item.ProductId} mudou. Por favor, atualize seu carrinho.");

            if (item.PaidPrice == 0 && product.Price > 0)
                throw new InvalidOperationException($"Preço inválido para o produto {item.ProductId}.");

            amountCents += (long)item.Quantity * item.PaidPrice;
            itemSnapshots.Add(new CheckoutItemSnapshot(
                item.ProductId, product.Name, item.Quantity,
                item.PaidPrice, product.Price, item.Observation));
        }

        // 3. Dedup — if this exact cart was submitted in the last 5 minutes, return the existing session.
        //    Prevents double-charging when the frontend retries due to a timeout or network error.
        var recent = await db.QueryFirstOrDefaultAsync<(Guid Id, string? CheckoutUrl)>(
            """
            SELECT "Id", "CheckoutUrl"
            FROM checkout_sessions
            WHERE "ClientMobile" = @Mobile
              AND "AmountCents"  = @Amount
              AND "UsedAt"       IS NULL
              AND "CreatedAt"    > @Threshold
            LIMIT 1
            """,
            new { Mobile = command.ClientMobile, Amount = amountCents, Threshold = DateTime.UtcNow.AddMinutes(-5) });

        if (recent.Id != default)
        {
            logger.LogInformation("Returning existing checkout session {SessionId} for {Mobile}", recent.Id, command.ClientMobile);
            return new CreateOrderResult(recent.Id, recent.CheckoutUrl!);
        }

        // 4. Create customer in AbacatePay
        var customerResponse = await abacatePay.CreateCustomerAsync(
            command.ClientName, command.ClientMobile, command.Email, command.TaxId);

        if (customerResponse?.Data is null)
        {
            logger.LogError("Falha ao criar customer AbacatePay");
            throw new PaymentGatewayException("Falha ao processar pagamento. Tente novamente.");
        }

        var abacateCustomerId = customerResponse.Data.Id;

        // 4. Create billing in AbacatePay
        var billingRequest = new CreateAbacateBillingRequest(
            Frequency: "ONE_TIME",
            Methods: ["PIX", "CARD"],
            Products: itemSnapshots.Select(s => new AbacateBillingProductRequest(
                ExternalId: s.ProductId.ToString(),
                Name: s.Name,
                Description: null,
                Quantity: s.Quantity,
                Price: s.PaidPrice
            )).ToArray(),
            ReturnUrl: config["AbacatePay:ReturnUrl"]!,
            CompletionUrl: config["AbacatePay:CompletionUrl"]!,
            CustomerId: abacateCustomerId
        );

        var billingResponse = await abacatePay.CreateBillingAsync(billingRequest);

        if (billingResponse?.Data is null)
        {
            logger.LogError("Falha ao criar billing AbacatePay");
            throw new PaymentGatewayException("Falha ao processar pagamento. Tente novamente.");
        }

        var billingData = billingResponse.Data;

        // 5. Store AbacateStoreProductId best-effort
        if (billingData.Products != null)
        {
            foreach (var bp in billingData.Products)
            {
                if (Guid.TryParse(bp.ExternalId, out var localProductId))
                {
                    try
                    {
                        await db.ExecuteAsync(
                            """
                            UPDATE products
                            SET "AbacateStoreProductId" = @AbacateId
                            WHERE "Id" = @LocalId
                              AND "AbacateStoreProductId" IS NULL
                            """,
                            new { AbacateId = bp.Id, LocalId = localProductId });
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Falha ao salvar AbacateStoreProductId para produto {ProductId}", localProductId);
                    }
                }
            }
        }

        // 6. Persist checkout session — the order will be created only after webhook confirmation
        var devMode = bool.Parse(config["AbacatePay:DevMode"] ?? "false");
        var itemsJson = JsonSerializer.Serialize(itemSnapshots, _json);
        var refsJson = allReferenceKeys.Count > 0
            ? JsonSerializer.Serialize(allReferenceKeys, _json)
            : null;

        await db.ExecuteAsync(
            """
            INSERT INTO checkout_sessions
                ("Id", "AbacateBillingId", "AbacateCustomerId", "ClientName", "ClientMobile",
                 "Email", "TaxId", "DeliveryDate", "ItemsJson", "ReferencesJson",
                 "AmountCents", "CheckoutUrl", "DevMode", "CreatedAt")
            VALUES
                (@Id, @AbacateBillingId, @AbacateCustomerId, @ClientName, @ClientMobile,
                 @Email, @TaxId, @DeliveryDate, @ItemsJson, @ReferencesJson,
                 @AmountCents, @CheckoutUrl, @DevMode, @CreatedAt)
            """,
            new
            {
                Id = sessionId,
                AbacateBillingId = billingData.Id,
                AbacateCustomerId = abacateCustomerId,
                ClientName = command.ClientName,
                ClientMobile = command.ClientMobile,
                Email = command.Email,
                TaxId = command.TaxId,
                DeliveryDate = command.DeliveryDate,
                ItemsJson = itemsJson,
                ReferencesJson = refsJson,
                AmountCents = amountCents,
                CheckoutUrl = billingData.Url,
                DevMode = devMode,
                CreatedAt = DateTime.UtcNow
            });

        logger.LogInformation("Checkout session {SessionId} created for billing {BillingId}", sessionId, billingData.Id);

        return new CreateOrderResult(sessionId, billingData.Url);
    }

    private static void Validate(CreateOrderCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.ClientName))
            throw new ArgumentException("clientName é obrigatório.");
        if (string.IsNullOrWhiteSpace(cmd.ClientMobile))
            throw new ArgumentException("clientMobile é obrigatório.");
        if (string.IsNullOrWhiteSpace(cmd.Email))
            throw new ArgumentException("email é obrigatório.");
        if (string.IsNullOrWhiteSpace(cmd.TaxId))
            throw new ArgumentException("taxId (CPF) é obrigatório.");
        if (cmd.DeliveryDate == default)
            throw new ArgumentException("deliveryDate inválido.");
        if (cmd.Items.Count == 0)
            throw new ArgumentException("O pedido deve ter pelo menos 1 item.");

        foreach (var item in cmd.Items)
        {
            if (item.Quantity <= 0)
                throw new ArgumentException($"Quantidade inválida para o produto {item.ProductId}.");
            if (item.PaidPrice < 100)
                throw new ArgumentException($"Preço mínimo inválido para o produto {item.ProductId}. Mínimo: R$ 1,00.");
        }
    }
}

// Snapshot of a cart item stored in checkout_sessions.ItemsJson
public record CheckoutItemSnapshot(
    Guid ProductId,
    string Name,
    int Quantity,
    int PaidPrice,
    int BasePrice,
    string? Observation);
