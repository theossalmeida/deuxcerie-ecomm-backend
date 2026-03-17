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
    DateTime DeliveryDate,
    IReadOnlyList<OrderItemCommand> Items);

public record CreateOrderResult(Guid OrderId, Guid ClientId, long TotalPaid);
