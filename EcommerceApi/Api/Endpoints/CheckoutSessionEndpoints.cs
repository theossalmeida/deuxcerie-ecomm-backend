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
            // Single atomic query eliminates TOCTOU race condition:
            // UsedAt and OrderId are read together — if UsedAt is committed, so is the order
            var row = await db.QueryFirstOrDefaultAsync<(Guid Id, DateTime? UsedAt, Guid? OrderId)>(
                """
                SELECT cs."Id", cs."UsedAt", o."Id" AS "OrderId"
                FROM checkout_sessions cs
                LEFT JOIN payment_transactions pt ON pt."AbacateBillingId" = cs."AbacateBillingId"
                LEFT JOIN orders o ON o."Id" = pt."OrderId"
                WHERE cs."Id" = @Id
                LIMIT 1
                """,
                new { Id = sessionId });

            if (row == default)
                return Results.NotFound(new { error = "Sessão não encontrada." });

            if (!row.UsedAt.HasValue)
                return Results.Ok(new { status = "pending", orderId = (Guid?)null });

            return Results.Ok(new { status = "paid", orderId = row.OrderId });
        })
        .RequireRateLimiting("paymentStatus");

        return app;
    }
}
