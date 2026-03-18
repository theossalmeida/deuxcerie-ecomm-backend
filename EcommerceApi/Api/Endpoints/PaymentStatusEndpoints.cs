using Dapper;
using EcommerceApi.Infrastructure.DTOs;
using System.Data;

namespace EcommerceApi.Api.Endpoints;

public static class PaymentStatusEndpoints
{
    public static IEndpointRouteBuilder MapPaymentStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/orders/{orderId:guid}/payment-status", async (
            Guid orderId,
            IDbConnection db) =>
        {
            var transaction = await db.QueryFirstOrDefaultAsync<PaymentTransactionDto>(
                """
                SELECT "Id","Status","CheckoutUrl","ReceiptUrl","PaymentMethod"
                FROM payment_transactions
                WHERE "OrderId" = @OrderId
                ORDER BY "CreatedAt" DESC
                LIMIT 1
                """,
                new { OrderId = orderId });

            if (transaction == null)
                return Results.NotFound(new { error = "Nenhuma transação encontrada." });

            var statusName = transaction.Status switch
            {
                1 => "Pending",
                2 => "Paid",
                3 => "Failed",
                4 => "Refunded",
                5 => "Disputed",
                _ => "Unknown"
            };

            return Results.Ok(new
            {
                orderId,
                paymentStatus = statusName,
                checkoutUrl  = transaction.Status == 1 ? transaction.CheckoutUrl : null,
                receiptUrl   = transaction.Status == 2 ? transaction.ReceiptUrl  : null
            });
        })
        .RequireRateLimiting("paymentStatus");

        return app;
    }
}
