using EcommerceApi.Application.Orders;
using System.Text.Json;

namespace EcommerceApi.Api.Endpoints;

public static class OrderEndpoints
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/orders", async (HttpRequest request, CreateOrderHandler handler) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Content-Type deve ser multipart/form-data." });

            var form = await request.ReadFormAsync();

            var clientName = form["clientName"].ToString();
            var clientMobile = form["clientMobile"].ToString();
            var deliveryDateRaw = form["deliveryDate"].ToString();
            var itemsJson = form["items"].ToString();

            if (string.IsNullOrWhiteSpace(itemsJson))
                return Results.BadRequest(new { error = "O campo 'items' é obrigatório." });

            List<OrderItemDto>? itemDtos;
            try
            {
                itemDtos = JsonSerializer.Deserialize<List<OrderItemDto>>(itemsJson, _jsonOptions);
            }
            catch
            {
                return Results.BadRequest(new { error = "JSON inválido no campo 'items'." });
            }

            if (itemDtos is null || itemDtos.Count == 0)
                return Results.BadRequest(new { error = "O pedido deve ter pelo menos 1 item." });

            if (!DateTime.TryParse(deliveryDateRaw, out var deliveryDate))
                return Results.BadRequest(new { error = "deliveryDate inválido. Use formato ISO (ex: 2026-03-25)." });

            deliveryDate = DateTime.SpecifyKind(deliveryDate, DateTimeKind.Utc);

            // Build OrderItemCommands, collecting files by naming convention ref_{itemIndex}_{fileIndex}
            var items = new List<OrderItemCommand>(itemDtos.Count);
            for (int i = 0; i < itemDtos.Count; i++)
            {
                var dto = itemDtos[i];
                var refs = new List<FileReference>();

                int j = 0;
                while (true)
                {
                    var file = form.Files.GetFile($"ref_{i}_{j}");
                    if (file is null) break;
                    refs.Add(new FileReference(file.OpenReadStream(), file.ContentType, file.FileName));
                    j++;
                }

                items.Add(new OrderItemCommand(dto.ProductId, dto.Quantity, dto.PaidPrice, dto.Observation, refs));
            }

            try
            {
                var result = await handler.HandleAsync(new CreateOrderCommand(clientName, clientMobile, deliveryDate, items));
                return Results.Created($"/api/v1/ecommerce/orders/{result.OrderId}", new
                {
                    orderId = result.OrderId,
                    clientId = result.ClientId,
                    totalPaid = result.TotalPaid
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
        })
        .DisableAntiforgery()
        .RequireRateLimiting("orders");

        return app;
    }

    private record OrderItemDto(Guid ProductId, int Quantity, int PaidPrice, string? Observation);
}
