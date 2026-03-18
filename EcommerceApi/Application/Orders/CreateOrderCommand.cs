namespace EcommerceApi.Application.Orders;

public record FileReference(Stream Data, string ContentType, string FileName);

public record OrderItemCommand(
    Guid ProductId,
    int Quantity,
    int PaidPrice,
    string? Observation,
    IReadOnlyList<FileReference> References);

public record CreateOrderCommand(
    string ClientName,
    string ClientMobile,
    string Email,
    string TaxId,
    DateTime DeliveryDate,
    IReadOnlyList<OrderItemCommand> Items);

// sessionId = checkout_sessions.Id — the actual orderId is assigned after payment confirmation
public record CreateOrderResult(Guid SessionId, string CheckoutUrl);
