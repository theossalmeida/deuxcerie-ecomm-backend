using Dapper;
using EcommerceApi.Infrastructure.DTOs;
using EcommerceApi.Infrastructure.Repositories;
using EcommerceApi.Infrastructure.Services;
using System.Data;
using System.Text.Json;

namespace EcommerceApi.Application.Orders;

public class PaymentGatewayException(string message) : Exception(message);

public class CreateOrderHandler(
    IDbConnection db,
    StorageService storage,
    AbacatePayService abacatePay,
    ProductRepository productRepo,
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

        // 2. Validate products and calculate base totals (critical security check — before charging)
        long baseCents = 0;
        var itemSnapshots = new List<CheckoutItemSnapshot>();
        var cardCheckoutItems = new List<AbacateCheckoutItem>();
        long cardAmountCents = 0;

        foreach (var item in command.Items)
        {
            var product = await productRepo.GetByIdAsync(item.ProductId);

            if (product is null || !product.ProductStatus)
                throw new InvalidOperationException($"Produto {item.ProductId} não encontrado ou inativo.");

            var expectedPrice = command.PaymentMethod == "CARD"
                ? (int)Math.Round(product.Price * 1.05)
                : product.Price;

            if (item.PaidPrice < expectedPrice)
                throw new InvalidOperationException(
                    $"O preço do produto {item.ProductId} mudou. Por favor, atualize seu carrinho.");

            if (product.Price == 0)
                throw new InvalidOperationException($"Preço inválido para o produto {item.ProductId}.");

            baseCents += (long)item.Quantity * product.Price;

            if (command.PaymentMethod == "CARD")
            {
                var cardPrice = (int)Math.Round(product.Price * 1.05);
                cardAmountCents += (long)item.Quantity * cardPrice;
                itemSnapshots.Add(new CheckoutItemSnapshot(
                    item.ProductId, product.Name, item.Quantity,
                    cardPrice, product.Price, item.Observation));

                var abacateProductId = product.AbacateStoreProductId;
                var priceOutdated = product.AbacateStoreProductPrice != cardPrice;

                if (abacateProductId is null || priceOutdated)
                {
                    if (priceOutdated && abacateProductId is not null)
                    {
                        logger.LogInformation(
                            "Preço do produto {ProductId} mudou ({Old} → {New}), recriando no AbacatePay",
                            product.Id, product.AbacateStoreProductPrice, cardPrice);
                        await abacatePay.DeleteProductAsync(abacateProductId);
                    }

                    var productResponse = await abacatePay.CreateProductAsync(
                        product.Id, product.Name, cardPrice, product.Description);

                    if (productResponse?.Data is null)
                    {
                        logger.LogError("Falha ao criar produto {ProductId} no AbacatePay", product.Id);
                        throw new PaymentGatewayException("Falha ao processar pagamento. Tente novamente.");
                    }

                    abacateProductId = productResponse.Data.Id;
                    await productRepo.UpdateAbacateProductAsync(product.Id, abacateProductId, cardPrice);
                }

                cardCheckoutItems.Add(new AbacateCheckoutItem(abacateProductId, item.Quantity));
            }
            else
            {
                itemSnapshots.Add(new CheckoutItemSnapshot(
                    item.ProductId, product.Name, item.Quantity,
                    product.Price, product.Price, item.Observation));
            }
        }

        var devMode = bool.Parse(config["AbacatePay:DevMode"] ?? "false");
        var itemsJson = JsonSerializer.Serialize(itemSnapshots, _json);
        var refsJson = allReferenceKeys.Count > 0 ? JsonSerializer.Serialize(allReferenceKeys, _json) : null;

        if (command.PaymentMethod == "PIX")
            return await HandlePixAsync(command, sessionId, baseCents, itemsJson, refsJson, devMode);
        else
            return await HandleCardAsync(command, sessionId, baseCents, cardAmountCents, cardCheckoutItems, itemsJson, refsJson, devMode);
    }

    private async Task<CreateOrderResult> HandlePixAsync(
        CreateOrderCommand command, Guid sessionId,
        long baseCents, string itemsJson, string? refsJson, bool devMode)
    {
        var pixAmount = baseCents;

        var pixResponse = await abacatePay.CreatePixTransparentAsync(pixAmount);

        if (pixResponse?.Data is null)
        {
            logger.LogError("Falha ao criar PIX transparent AbacatePay");
            throw new PaymentGatewayException("Falha ao processar pagamento. Tente novamente.");
        }

        var pix = pixResponse.Data;

        await db.ExecuteAsync(
            """
            INSERT INTO checkout_sessions
                ("Id", "AbacateBillingId", "AbacateCustomerId", "ClientName", "ClientMobile",
                 "Email", "TaxId", "DeliveryDate", "ItemsJson", "ReferencesJson",
                 "AmountCents", "CheckoutUrl", "DevMode", "CreatedAt")
            VALUES
                (@Id, @AbacateBillingId, NULL, @ClientName, @ClientMobile,
                 @Email, @TaxId, @DeliveryDate, @ItemsJson, @ReferencesJson,
                 @AmountCents, NULL, @DevMode, @CreatedAt)
            """,
            new
            {
                Id = sessionId,
                AbacateBillingId = pix.Id,
                ClientName = command.ClientName,
                ClientMobile = command.ClientMobile,
                Email = command.Email,
                TaxId = command.TaxId,
                DeliveryDate = command.DeliveryDate,
                ItemsJson = itemsJson,
                ReferencesJson = refsJson,
                AmountCents = pixAmount,
                DevMode = devMode,
                CreatedAt = DateTime.UtcNow
            });

        logger.LogInformation("PIX session {SessionId} created — transparent {PixId}", sessionId, pix.Id);

        return new CreateOrderResult(sessionId, "PIX", null, pix.BrCode, pix.BrCodeBase64, pix.ExpiresAt);
    }

    private async Task<CreateOrderResult> HandleCardAsync(
        CreateOrderCommand command, Guid sessionId,
        long baseCents, long cardAmountCents,
        List<AbacateCheckoutItem> checkoutItems,
        string itemsJson, string? refsJson, bool devMode)
    {
        // Dedup — same cart submitted twice in 5 minutes
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
            new { Mobile = command.ClientMobile, Amount = cardAmountCents, Threshold = DateTime.UtcNow.AddMinutes(-5) });

        if (recent.Id != default)
        {
            logger.LogInformation("Returning existing card session {SessionId} for {Mobile}", recent.Id, command.ClientMobile);
            return new CreateOrderResult(recent.Id, "CARD", recent.CheckoutUrl!, null, null, null);
        }

        var linkRequest = new CreatePaymentLinkRequest(
            Frequency: "MULTIPLE_PAYMENTS",
            Items: checkoutItems.ToArray(),
            Methods: ["CARD"],
            ExternalId: sessionId.ToString(),
            ReturnUrl: $"{config["AbacatePay:ReturnUrl"]}?session={sessionId}",
            CompletionUrl: config["AbacatePay:CompletionUrl"]!
        );

        var linkResponse = await abacatePay.CreatePaymentLinkAsync(linkRequest);

        if (linkResponse?.Data is null)
        {
            logger.LogError("Falha ao criar payment link AbacatePay");
            throw new PaymentGatewayException("Falha ao processar pagamento. Tente novamente.");
        }

        var link = linkResponse.Data;

        await db.ExecuteAsync(
            """
            INSERT INTO checkout_sessions
                ("Id", "AbacateBillingId", "AbacateCustomerId", "ClientName", "ClientMobile",
                 "Email", "TaxId", "DeliveryDate", "ItemsJson", "ReferencesJson",
                 "AmountCents", "CheckoutUrl", "DevMode", "CreatedAt")
            VALUES
                (@Id, @AbacateBillingId, NULL, @ClientName, @ClientMobile,
                 @Email, @TaxId, @DeliveryDate, @ItemsJson, @ReferencesJson,
                 @AmountCents, @CheckoutUrl, @DevMode, @CreatedAt)
            """,
            new
            {
                Id = sessionId,
                AbacateBillingId = link.Id,
                ClientName = command.ClientName,
                ClientMobile = command.ClientMobile,
                Email = command.Email,
                TaxId = command.TaxId,
                DeliveryDate = command.DeliveryDate,
                ItemsJson = itemsJson,
                ReferencesJson = refsJson,
                AmountCents = cardAmountCents,
                CheckoutUrl = link.Url,
                DevMode = devMode,
                CreatedAt = DateTime.UtcNow
            });

        logger.LogInformation("Card session {SessionId} created — payment link {LinkId}", sessionId, link.Id);

        return new CreateOrderResult(sessionId, "CARD", link.Url, null, null, null);
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
        var minDeliveryDate = DateTime.UtcNow.Date.AddDays(2);
        if (cmd.DeliveryDate.Date < minDeliveryDate)
            throw new ArgumentException($"Data de entrega mínima é {minDeliveryDate:dd/MM/yyyy}.");
        if (cmd.Items.Count == 0)
            throw new ArgumentException("O pedido deve ter pelo menos 1 item.");
        if (cmd.PaymentMethod != "PIX" && cmd.PaymentMethod != "CARD")
            throw new ArgumentException("paymentMethod deve ser 'PIX' ou 'CARD'.");

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
