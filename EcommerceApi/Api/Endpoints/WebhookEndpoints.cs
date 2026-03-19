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
            EmailService email,
            IConfiguration config,
            ILogger<Program> logger) =>
        {
            var receivedAt = DateTime.UtcNow;

            if (!ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? true)
                return Results.BadRequest(new { error = "Content-Type deve ser application/json." });

            // Limit webhook body to 1 MB (prevents memory DoS)
            ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()
                ?.MaxRequestBodySize = 1 * 1024 * 1024;

            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            var rawBody = await reader.ReadToEndAsync();
            ctx.Request.Body.Position = 0;

            // Sanitized copy for logging (removes customer PII)
            var sanitizedBody = SanitizeWebhookPayload(rawBody);

            var signatureHeader = ctx.Request.Headers["X-Webhook-Signature"].ToString();

            // CAMADA 1 — webhookSecret (aceita secret principal ou secret de refund)
            var receivedSecret = ctx.Request.Query["webhookSecret"].ToString();
            var mainSecret = config["AbacatePay:WebhookSecret"] ?? "";
            var refundSecret = config["AbacatePay:RefundWebhookSecret"] ?? "";
            var secretValid = validator.ValidateWebhookSecret(receivedSecret, mainSecret)
                || (!string.IsNullOrEmpty(refundSecret) && validator.ValidateWebhookSecret(receivedSecret, refundSecret));

            if (!secretValid)
            {
                await LogWebhook(db, receivedAt, null, sanitizedBody, signatureHeader,
                    false, false, "InvalidSecret", "WebhookSecret inválido", null, 401);
                return Results.Unauthorized();
            }

            // CAMADA 2 — Parse payload
            WebhookPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<WebhookPayload>(rawBody, _json);
            }
            catch
            {
                await LogWebhook(db, receivedAt, null, sanitizedBody, signatureHeader,
                    true, true, "ParseError", "JSON inválido", null, 200);
                return Results.Ok();
            }

            var eventType = payload?.Event;
            var billingId = payload?.Data?.Checkout?.Id
                ?? payload?.Data?.Billing?.Id
                ?? payload?.Data?.Transparent?.Id;

            if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(billingId))
            {
                await LogWebhook(db, receivedAt, eventType, sanitizedBody, signatureHeader,
                    true, true, "InvalidPayload", "event ou billing id ausente", billingId, 200);
                return Results.Ok();
            }

            var expectedDevMode = bool.Parse(config["AbacatePay:DevMode"] ?? "false");
            if (payload!.DevMode != expectedDevMode)
            {
                logger.LogWarning("Webhook DevMode mismatch: received {Received}, expected {Expected}", payload.DevMode, expectedDevMode);
                await LogWebhook(db, receivedAt, eventType, sanitizedBody, signatureHeader,
                    true, true, "DevModeMismatch", $"DevMode mismatch: recebido {payload.DevMode}", billingId, 200);
                return Results.Ok();
            }

            var knownEvents = new[]
            {
                // Success
                "checkout.completed",
                "transparent.completed",
                // Refund
                "checkout.refunded",
                "transparent.refunded",
                // Dispute
                "checkout.disputed",
                "transparent.disputed",
                // Subscription
                "subscription.completed",
                "subscription.renewed",
                "subscription.cancelled",
            };

            if (!knownEvents.Contains(eventType))
            {
                await LogWebhook(db, receivedAt, eventType, sanitizedBody, signatureHeader,
                    true, true, "UnknownEvent", $"Evento desconhecido: {eventType}", billingId, 200);
                return Results.Ok();
            }

            try
            {
                switch (eventType)
                {
                    case "checkout.completed":
                    case "transparent.completed":
                    case "subscription.completed":
                    case "subscription.renewed":
                        await ProcessPaymentSuccessAsync(db, config, email, logger, payload, billingId, receivedAt, eventType);
                        break;

                    case "checkout.refunded":
                    case "transparent.refunded":
                        await ProcessPaymentFailureAsync(db, email, logger, billingId, 4, "Pagamento estornado", eventType, receivedAt);
                        break;

                    case "checkout.disputed":
                    case "transparent.disputed":
                        await ProcessPaymentFailureAsync(db, email, logger, billingId, 5, "Pagamento disputado", eventType, receivedAt);
                        break;

                    case "subscription.cancelled":
                        await UpdateTransactionStatus(db, billingId, 6, "Assinatura cancelada", eventType, receivedAt);
                        break;
                }

                await LogWebhook(db, receivedAt, eventType, sanitizedBody, signatureHeader,
                    true, true, "Processed", null, billingId, 200);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro ao processar webhook {EventType} para billing {BillingId}", eventType, billingId);
                await LogWebhook(db, receivedAt, eventType, sanitizedBody, signatureHeader,
                    true, true, "ProcessingError", ex.Message, billingId, 200);
            }

            return Results.Ok();
        })
        .RequireRateLimiting("webhooks");

        return app;
    }

    private static async Task ProcessPaymentSuccessAsync(
        IDbConnection db, IConfiguration config, EmailService email, ILogger logger,
        WebhookPayload payload, string billingId, DateTime receivedAt, string eventType)
    {
        // Idempotency
        var existingOrderId = await db.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT pt."OrderId" FROM payment_transactions pt
            WHERE pt."AbacateBillingId" = @BillingId
            LIMIT 1
            """,
            new { BillingId = billingId });

        if (existingOrderId.HasValue)
            return;

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
            logger.LogWarning("{Event} recebido para billing {BillingId} sem checkout_session correspondente", eventType, billingId);
            return;
        }

        if (session.UsedAt.HasValue)
            return;

        // CAMADA 5 — Amount + status validation (same rules for PIX and CARD)
        // Both flows: verify id (already done via DB lookup), amount, and status == "PAID"
        var transparent = payload.Data?.Transparent;
        var checkout = payload.Data?.Checkout;
        var billingData = payload.Data?.Billing;

        var receivedAmount = transparent?.Amount ?? checkout?.Amount ?? billingData?.Amount;
        var receivedStatus = transparent?.Status ?? checkout?.Status ?? billingData?.Status;

        if (receivedAmount is null)
        {
            logger.LogCritical("ALERTA CRÍTICO: amount ausente no webhook {Event} para billing {BillingId}", eventType, billingId);
            return;
        }

        if (receivedAmount.Value != session.AmountCents)
        {
            logger.LogCritical(
                "ALERTA CRÍTICO: valor divergente para billing {BillingId}. Esperado: {Expected}, Recebido: {Received}",
                billingId, session.AmountCents, receivedAmount.Value);
            return;
        }

        if (!string.Equals(receivedStatus, "PAID", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Webhook {Event} recebido mas status={Status} para billing {BillingId} — ignorado",
                eventType, receivedStatus, billingId);
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

            long totalPaid = items.Sum(i => (long)i.Quantity * i.PaidPrice);
            long totalValue = items.Sum(i => (long)i.Quantity * i.BasePrice);

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
                cmd.Parameters.AddWithValue("Status", 4);
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

            var customer = payload.Data?.Customer;
            var payerInfo = payload.Data?.PayerInformation;
            var payerPix = payerInfo?.PIX;
            var payerCard = payerInfo?.CARD;
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
                    Status = 2,
                    AmountCents = session.AmountCents,
                    PaidAmountCents = payload.Data?.Payment?.Amount ?? checkout?.PaidAmount ?? transparent?.Amount ?? billingData?.PaidAmount,
                    PlatformFeeCents = payload.Data?.Payment?.Fee != 0 ? payload.Data?.Payment?.Fee : checkout?.PlatformFee ?? transparent?.PlatformFee ?? billingData?.PlatformFee,
                    CheckoutUrl = session.CheckoutUrl,
                    ReceiptUrl = checkout?.ReceiptUrl ?? transparent?.ReceiptUrl ?? billingData?.ReceiptUrl,
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

            await conn.ExecuteAsync(
                """UPDATE checkout_sessions SET "UsedAt" = @UsedAt WHERE "AbacateBillingId" = @BillingId""",
                new { UsedAt = now, BillingId = billingId },
                transaction: tx);

            tx.Commit();

            logger.LogInformation("Order {OrderId} created from billing {BillingId} [{Event}]", orderId, billingId, eventType);

            // Send confirmation email (best-effort — never throw)
            _ = email.SendOrderConfirmedAsync(session.Email, session.ClientName, orderId);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static async Task ProcessPaymentFailureAsync(
        IDbConnection db, EmailService email, ILogger logger,
        string billingId, int status, string reason, string eventType, DateTime receivedAt)
    {
        // Idempotency — prevent duplicate emails on webhook replay
        var existingStatus = await db.QueryFirstOrDefaultAsync<int?>(
            """SELECT "Status" FROM payment_transactions WHERE "AbacateBillingId" = @BillingId LIMIT 1""",
            new { BillingId = billingId });

        if (existingStatus == status) return;

        await UpdateTransactionStatus(db, billingId, status, reason, eventType, receivedAt);

        // Cancel the order and its items when refunded
        Guid? orderId = null;
        if (status == 4)
            orderId = await CancelOrderAsync(db, billingId, receivedAt);

        // Look up customer info to send notification email
        var session = await db.QueryFirstOrDefaultAsync<(string Email, string ClientName)>(
            """
            SELECT cs."Email", cs."ClientName"
            FROM checkout_sessions cs
            WHERE cs."AbacateBillingId" = @BillingId
            LIMIT 1
            """,
            new { BillingId = billingId });

        if (session == default)
        {
            logger.LogWarning("Nenhuma session encontrada para billing {BillingId} ao enviar email de falha", billingId);
            return;
        }

        if (status == 4)
            _ = email.SendOrderCancelledAsync(session.Email, session.ClientName, orderId);
        else if (status == 5)
            _ = email.SendPaymentDisputedAsync(session.Email, session.ClientName);
    }

    private static async Task<Guid?> CancelOrderAsync(IDbConnection db, string billingId, DateTime now)
    {
        var orderId = await db.QueryFirstOrDefaultAsync<Guid?>(
            """SELECT "OrderId" FROM payment_transactions WHERE "AbacateBillingId" = @BillingId LIMIT 1""",
            new { BillingId = billingId });

        if (orderId is null)
            return null;

        // Status 5 = Cancelled
        await db.ExecuteAsync(
            """UPDATE orders SET "Status" = 5, "UpdatedAt" = @Now WHERE "Id" = @OrderId""",
            new { Now = now, OrderId = orderId.Value });

        await db.ExecuteAsync(
            """UPDATE order_items SET "ItemCanceled" = true, "UpdatedAt" = @Now WHERE "OrderId" = @OrderId""",
            new { Now = now, OrderId = orderId.Value });

        return orderId;
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

    private static string SanitizeWebhookPayload(string rawBody)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(rawBody);
            if (node is System.Text.Json.Nodes.JsonObject root &&
                root["data"] is System.Text.Json.Nodes.JsonObject data)
            {
                data.Remove("customer");
                data.Remove("payerInformation");
            }
            return node?.ToJsonString() ?? rawBody;
        }
        catch
        {
            return rawBody;
        }
    }
}
