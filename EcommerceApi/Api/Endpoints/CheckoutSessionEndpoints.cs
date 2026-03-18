using Dapper;
using System.Data;

namespace EcommerceApi.Api.Endpoints;

public static class CheckoutSessionEndpoints
{
    public static IEndpointRouteBuilder MapCheckoutSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/checkout-sessions/{sessionId:guid}/status", async (
            Guid sessionId,
            IDbConnection db) =>
        {
            var row = await db.QueryFirstOrDefaultAsync<(Guid Id, DateTime? UsedAt)>(
                """
                SELECT "Id", "UsedAt"
                FROM checkout_sessions
                WHERE "Id" = @Id
                LIMIT 1
                """,
                new { Id = sessionId });

            if (row == default)
                return Results.NotFound(new { error = "Sessão não encontrada." });

            if (!row.UsedAt.HasValue)
                return Results.Ok(new { status = "pending", orderId = (Guid?)null });

            var orderId = await db.QueryFirstOrDefaultAsync<Guid?>(
                """
                SELECT o."Id"
                FROM orders o
                INNER JOIN payment_transactions pt ON pt."OrderId" = o."Id"
                INNER JOIN checkout_sessions cs ON cs."AbacateBillingId" = pt."AbacateBillingId"
                WHERE cs."Id" = @SessionId
                LIMIT 1
                """,
                new { SessionId = sessionId });

            return Results.Ok(new { status = "paid", orderId });
        })
        .RequireRateLimiting("paymentStatus");

        return app;
    }
}
