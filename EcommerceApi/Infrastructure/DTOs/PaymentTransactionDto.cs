namespace EcommerceApi.Infrastructure.DTOs;

public record PaymentTransactionDto(
    Guid Id,
    Guid OrderId,
    string AbacateBillingId,
    string? AbacateCustomerId,
    string PaymentMethod,
    int Status,
    long AmountCents,
    long? PaidAmountCents,
    long? PlatformFeeCents,
    string? CheckoutUrl,
    string? ReceiptUrl,
    string? PayerName,
    string? PayerTaxIdMasked,
    string? CardLastFour,
    string? CardBrand,
    DateTime? WebhookReceivedAt,
    string? WebhookEventType,
    string IdempotencyKey,
    string? FailureReason,
    bool DevMode,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);
