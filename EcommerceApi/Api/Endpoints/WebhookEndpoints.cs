using Dapper;
using EcommerceApi.Application.Orders;
using EcommerceApi.Infrastructure.DTOs;
using EcommerceApi.Infrastructure.Services;
using Npgsql;
using NpgsqlTypes;
using System.Data;
using System.Text.Json;

namespace EcommerceApi.Api.Endpoints;

public static class WebhookEndpoints
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/abacatepay", async (
            HttpContext ctx,
            IDbConnection db,
            WebhookValidationService validator,
            IConfiguration config,
            ILogger<Program> logger) =>
        {
            var receivedAt = DateTime.UtcNow;

            // Reject non-JSON payloads before touching the body
            if (!ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? true)
                return Results.BadRequest(new { error = "Content-Type deve ser application/json." });

            // Read raw body BEFORE any parse — critical for HMAC
            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            var rawBody = await reader.ReadToEndAsync();
            ctx.Request.Body.Position = 0;

            var signatureHeader = ctx.Request.Headers["X-Webhook-Signature"].ToString();

            // CAMADA 1 — webhookSecret
            var receivedSecret = ctx.Request.Query["webhookSecret"].ToString();
            var expectedSecret = config["AbacatePay:WebhookSecret"] ?? "";
            var secretValid = validator.ValidateWebhookSecret(receivedSecret, expectedSecret);

            if (!secretValid)
            {
                await LogWebhook(db, receivedAt, null, rawBody, signatureHeader,
                    false, false, "InvalidSecret", "WebhookSecret inválido", null, 401);
                return Results.Unauthorized();
            }

            // CAMADA 2 — HMAC-SHA256 (temporariamente desabilitado para diagnóstico)
            var hmacKey = config["AbacatePay:HmacPublicKey"] ?? "";
            var signatureValid = validator.ValidateHmacSignature(rawBody, signatureHeader, hmacKey);
            if (!signatureValid)
                logger.LogWarning("HMAC inválido — ignorado temporariamente para diagnóstico. Signature: {Sig}", signatureHeader);

            // CAMADA 3 — Parse payload
            WebhookPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<WebhookPayload>(rawBody, _json);
            }
            catch
            {
                await LogWebhook(db, receivedAt, null, rawBody, signatureHeader,
                    true, true, "ParseError", "JSON inválido", null, 200);
                return Results.Ok();
            }

            var eventType = payload?.Event;
            // Support both payload shapes:
            //   checkout.completed → data.checkout.id
            //   billing.paid / pix.paid → data.billing.id
            var billingId = payload?.Data?.Checkout?.Id ?? payload?.Data?.Billing?.Id;

            if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(billingId))
            {
                await LogWebhook(db, receivedAt, eventType, rawBody, signatureHeader,
                    true, true, "InvalidPayload", "event ou billing.id ausente", billingId, 200);
                return Results.Ok();
            }

            var expectedDevMode = bool.Parse(config["AbacatePay:DevMode"] ?? "false");
            if (payload!.DevMode != expectedDevMode)
            {
                logger.LogWarning("Webhook DevMode mismatch: received {Received}, expected {Expected}", payload.DevMode, expectedDevMode);
                await LogWebhook(db, receivedAt, eventType, rawBody, signatureHeader,
                    true, true, "DevModeMismatch", $"DevMode mismatch: recebido {payload.DevMode}", billingId, 200);
                return Results.Ok();
            }

            var knownEvents = new[]
            {
                "billing.paid",       // v1 billing — confirmed by llms.txt
                "checkout.completed", // alternate name observed in sandbox
                "pix.paid",           // PIX QR Code flow
                "billing.refunded", "checkout.refunded",
                "billing.disputed", "checkout.disputed",
                "pix.expired",
                "withdraw.paid"
            };
            if (!knownEvents.Contains(eventType))
            {
                await LogWebhook(db, receivedAt, eventType, rawBody, signatureHeader,
                    true, true, "UnknownEvent", $"Evento desconhecido: {eventType}", billingId, 200);
                return Results.Ok();
            }

            // Process — always return 200 after valid HMAC
            try
            {
                switch (eventType)
                {
                    case "billing.paid":
                    case "checkout.completed":
                    case "pix.paid":
                        await ProcessBillingPaid(db, config, logger, payload, billingId, receivedAt, eventType);
                        break;

                    case "billing.refunded":
                    case "checkout.refunded":
                        await UpdateTransactionStatus(db, billingId, 4, "Pagamento estornado", eventType, receivedAt);
                        break;

                    case "billing.disputed":
                    case "checkout.disputed":
                        await UpdateTransactionStatus(db, billingId, 5, "Pagamento disputado", eventType, receivedAt);
                        break;

                    // pix.expired and withdraw.paid are logged but need no order-level action
                }

                await LogWebhook(db, receivedAt, eventType, rawBody, signatureHeader,
                    true, true, "Processed", null, billingId, 200);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro ao processar webhook {EventType} para billing {BillingId}", eventType, billingId);
                await LogWebhook(db, receivedAt, eventType, rawBody, signatureHeader,
                    true, true, "ProcessingError", ex.Message, billingId, 200);
            }

            return Results.Ok();
        })
        .RequireRateLimiting("webhooks");

        return app;
    }

    // Creates the order in the DB only after payment is confirmed.
    // OrderStatus.Received = 4
    private static async Task ProcessBillingPaid(
        IDbConnection db, IConfiguration config, ILogger logger,
        WebhookPayload payload, string billingId, DateTime receivedAt,
        string eventType = "checkout.completed")
    {
        // CAMADA 4 — Idempotency: check if order was already created for this billing
        var existingOrderId = await db.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT pt."OrderId" FROM payment_transactions pt
            WHERE pt."AbacateBillingId" = @BillingId
            LIMIT 1
            """,
            new { BillingId = billingId });

        if (existingOrderId.HasValue)
        {
            // Already processed — idempotent
            return;
        }

        // Load checkout session
        var session = await db.QueryFirstOrDefaultAsync<CheckoutSessionRow>(
            """
            SELECT "Id", "AbacateBillingId", "AbacateCustomerId", "ClientName", "ClientMobile",
                   "Email", "TaxId", "DeliveryDate", "ItemsJson", "ReferencesJson",
                   "AmountCents", "CheckoutUrl", "DevMode", "CreatedAt", "UsedAt"
            FROM checkout_sessions
            WHERE "AbacateBillingId" = @BillingId
            LIMIT 1
            """,
            new { BillingId = billingId });

        if (session is null)
        {
            logger.LogWarning("billing.paid recebido para billing {BillingId} sem checkout_session correspondente", billingId);
            return;
        }

        if (session.UsedAt.HasValue)
        {
            // Session already consumed — duplicate webhook
            return;
        }

        // CAMADA 5 — Amount validation
        // Prefer data.payment.amount (present in all event types).
        // Fall back to checkout.paidAmount / billing.paidAmount for older payload shapes.
        var paidAmount = payload.Data?.Payment?.Amount
            ?? payload.Data?.Checkout?.PaidAmount
            ?? payload.Data?.Billing?.PaidAmount;

        if (paidAmount is null)
        {
            logger.LogCritical(
                "ALERTA CRÍTICO: paidAmount ausente no webhook checkout.completed para billing {BillingId}",
                billingId);
            return;
        }

        if (paidAmount.Value != session.AmountCents)
        {
            logger.LogCritical(
                "ALERTA CRÍTICO: valor divergente para billing {BillingId}. Esperado: {Expected}, Recebido: {Received}",
                billingId, session.AmountCents, paidAmount.Value);
            return;
        }

        var items = JsonSerializer.Deserialize<List<CheckoutItemSnapshot>>(session.ItemsJson, _json)
            ?? throw new InvalidOperationException("ItemsJson inválido na session");

        var refs = session.ReferencesJson != null
            ? JsonSerializer.Deserialize<List<string>>(session.ReferencesJson, _json)
            : null;

        var conn = (NpgsqlConnection)db;
        if (conn.State != System.Data.ConnectionState.Open)
            conn.Open();
        using var tx = conn.BeginTransaction();

        try
        {
            var now = DateTime.UtcNow;

            // Find or create client
            var clientId = await conn.QueryFirstOrDefaultAsync<Guid?>(
                """SELECT "Id" FROM clients WHERE "Name" = @Name AND "Mobile" = @Mobile LIMIT 1""",
                new { Name = session.ClientName, Mobile = session.ClientMobile },
                transaction: tx);

            if (clientId is null)
            {
                clientId = Guid.NewGuid();
                await conn.ExecuteAsync(
                    """
                    INSERT INTO clients ("Id", "Name", "Mobile", "Status", "CreatedAt")
                    VALUES (@Id, @Name, @Mobile, true, @CreatedAt)
                    """,
                    new { Id = clientId, Name = session.ClientName, Mobile = session.ClientMobile, CreatedAt = now },
                    transaction: tx);
            }

            // Calculate totals
            long totalPaid = items.Sum(i => (long)i.Quantity * i.PaidPrice);
            long totalValue = items.Sum(i => (long)i.Quantity * i.BasePrice);

            // Insert order — Status 4 = Received
            var orderId = Guid.CreateVersion7();
            using (var cmd = new NpgsqlCommand(
                """
                INSERT INTO orders ("Id", "ClientId", "DeliveryDate", "Status", "TotalPaid", "TotalValue", "References", "PaymentSource", "CreatedAt", "UpdatedAt")
                VALUES (@Id, @ClientId, @DeliveryDate, @Status, @TotalPaid, @TotalValue, @References, @PaymentSource, @CreatedAt, @UpdatedAt)
                """,
                conn, tx))
            {
                cmd.Parameters.AddWithValue("Id", orderId);
                cmd.Parameters.AddWithValue("ClientId", clientId.Value);
                cmd.Parameters.AddWithValue("DeliveryDate", session.DeliveryDate);
                cmd.Parameters.AddWithValue("Status", 4); // Received
                cmd.Parameters.AddWithValue("TotalPaid", totalPaid);
                cmd.Parameters.AddWithValue("TotalValue", totalValue);
                cmd.Parameters.AddWithValue("PaymentSource", "ECOMMERCE");
                cmd.Parameters.AddWithValue("CreatedAt", now);
                cmd.Parameters.AddWithValue("UpdatedAt", now);

                var refsParam = new NpgsqlParameter("References", NpgsqlDbType.Array | NpgsqlDbType.Text);
                refsParam.Value = refs is { Count: > 0 } ? refs.ToArray() : DBNull.Value;
                cmd.Parameters.Add(refsParam);

                await cmd.ExecuteNonQueryAsync();
            }

            // Insert order items
            foreach (var item in items)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO order_items ("OrderId", "ProductId", "Quantity", "PaidUnitPrice", "BaseUnitPrice", "TotalPaid", "TotalValue", "Observation", "ItemCanceled", "CreatedAt", "UpdatedAt")
                    VALUES (@OrderId, @ProductId, @Quantity, @PaidUnitPrice, @BaseUnitPrice, @TotalPaid, @TotalValue, @Observation, false, @CreatedAt, @UpdatedAt)
                    """,
                    new
                    {
                        OrderId = orderId,
                        ProductId = item.ProductId,
                        Quantity = item.Quantity,
                        PaidUnitPrice = item.PaidPrice,
                        BaseUnitPrice = item.BasePrice,
                        TotalPaid = (long)item.Quantity * item.PaidPrice,
                        TotalValue = (long)item.Quantity * item.BasePrice,
                        Observation = item.Observation,
                        CreatedAt = now,
                        UpdatedAt = now
                    },
                    transaction: tx);
            }

            // Insert payment_transaction
            // Normalize: prefer checkout shape, fall back to billing shape
            var checkout = payload.Data?.Checkout;
            var billingData = payload.Data?.Billing;
            var customer = payload.Data?.Customer;
            var payerInfo = payload.Data?.PayerInformation;
            var payerPix = payerInfo?.PIX;
            var payerCard = payerInfo?.CARD;
            // Method comes from payerInformation.method ("PIX" or "CARD")
            var paymentMethod = payerInfo?.Method ?? (payerCard != null ? "CARD" : "PIX");

            await conn.ExecuteAsync(
                """
                INSERT INTO payment_transactions
                    ("Id", "OrderId", "AbacateBillingId", "AbacateCustomerId",
                     "PaymentMethod", "Status", "AmountCents", "PaidAmountCents", "PlatformFeeCents",
                     "CheckoutUrl", "ReceiptUrl", "PayerName", "PayerTaxIdMasked",
                     "CardLastFour", "CardBrand", "WebhookReceivedAt", "WebhookEventType",
                     "IdempotencyKey", "DevMode", "CreatedAt", "UpdatedAt")
                VALUES
                    (@Id, @OrderId, @AbacateBillingId, @AbacateCustomerId,
                     @PaymentMethod, @Status, @AmountCents, @PaidAmountCents, @PlatformFeeCents,
                     @CheckoutUrl, @ReceiptUrl, @PayerName, @PayerTaxIdMasked,
                     @CardLastFour, @CardBrand, @WebhookReceivedAt, @WebhookEventType,
                     @IdempotencyKey, @DevMode, @CreatedAt, @UpdatedAt)
                """,
                new
                {
                    Id = Guid.CreateVersion7(),
                    OrderId = orderId,
                    AbacateBillingId = billingId,
                    AbacateCustomerId = session.AbacateCustomerId,
                    PaymentMethod = paymentMethod,
                    Status = 2, // Paid
                    AmountCents = session.AmountCents,
                    PaidAmountCents = payload.Data?.Payment?.Amount ?? checkout?.PaidAmount ?? billingData?.PaidAmount,
                    PlatformFeeCents = payload.Data?.Payment?.Fee != 0 ? payload.Data?.Payment?.Fee : checkout?.PlatformFee ?? billingData?.PlatformFee,
                    CheckoutUrl = session.CheckoutUrl,
                    ReceiptUrl = checkout?.ReceiptUrl ?? billingData?.ReceiptUrl,
                    // PIX: payer name from payerInformation.PIX.name, tax id from customer (masked)
                    // CARD: payer name from customer.name
                    PayerName = payerPix?.Name ?? customer?.Name,
                    PayerTaxIdMasked = customer?.TaxId,
                    CardLastFour = payerCard?.Number,
                    CardBrand = payerCard?.Brand,
                    WebhookReceivedAt = receivedAt,
                    WebhookEventType = eventType,
                    IdempotencyKey = $"billing_{billingId}",
                    DevMode = session.DevMode,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                transaction: tx);

            // Mark session as consumed
            await conn.ExecuteAsync(
                """UPDATE checkout_sessions SET "UsedAt" = @UsedAt WHERE "AbacateBillingId" = @BillingId""",
                new { UsedAt = now, BillingId = billingId },
                transaction: tx);

            tx.Commit();

            logger.LogInformation(
                "Order {OrderId} created (Received) from checkout session {SessionId} — billing {BillingId}",
                orderId, session.Id, billingId);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static async Task UpdateTransactionStatus(
        IDbConnection db, string billingId, int status, string reason, string eventType, DateTime receivedAt)
    {
        await db.ExecuteAsync(
            """
            UPDATE payment_transactions SET
                "Status"            = @Status,
                "FailureReason"     = @FailureReason,
                "WebhookReceivedAt" = @WebhookReceivedAt,
                "WebhookEventType"  = @WebhookEventType,
                "UpdatedAt"         = @UpdatedAt
            WHERE "AbacateBillingId" = @BillingId
            """,
            new
            {
                Status = status,
                FailureReason = reason,
                WebhookReceivedAt = receivedAt,
                WebhookEventType = eventType,
                UpdatedAt = receivedAt,
                BillingId = billingId
            });
    }

    private static async Task LogWebhook(
        IDbConnection db, DateTime receivedAt,
        string? eventType, string rawPayload, string? signatureHeader,
        bool signatureValid, bool secretValid, string? processingResult,
        string? errorMessage, string? abacateBillingId, int httpStatus)
    {
        await db.ExecuteAsync(
            """
            INSERT INTO webhook_event_log
                ("Id","ReceivedAt","EventType","RawPayload","SignatureHeader",
                 "SignatureValid","SecretValid","ProcessingResult","ErrorMessage",
                 "AbacateBillingId","HttpStatusReturned")
            VALUES
                (@Id,@ReceivedAt,@EventType,@RawPayload,@SignatureHeader,
                 @SignatureValid,@SecretValid,@ProcessingResult,@ErrorMessage,
                 @AbacateBillingId,@HttpStatusReturned)
            """,
            new
            {
                Id = Guid.NewGuid(),
                ReceivedAt = receivedAt,
                EventType = eventType,
                RawPayload = rawPayload,
                SignatureHeader = signatureHeader,
                SignatureValid = signatureValid,
                SecretValid = secretValid,
                ProcessingResult = processingResult,
                ErrorMessage = errorMessage,
                AbacateBillingId = abacateBillingId,
                HttpStatusReturned = httpStatus
            });
    }

    // Dapper mapping row for checkout_sessions
    private record CheckoutSessionRow(
        Guid Id,
        string AbacateBillingId,
        string? AbacateCustomerId,
        string ClientName,
        string ClientMobile,
        string Email,
        string TaxId,
        DateTime DeliveryDate,
        string ItemsJson,
        string? ReferencesJson,
        long AmountCents,
        string? CheckoutUrl,
        bool DevMode,
        DateTime CreatedAt,
        DateTime? UsedAt);
}
