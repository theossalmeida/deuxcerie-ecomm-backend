namespace EcommerceApi.Infrastructure.DTOs;

// ---------- Requests ----------

public record CreateAbacateCustomerRequest(
    string Name,
    string Cellphone,
    string Email,
    string TaxId
);

public record AbacateBillingProductRequest(
    string ExternalId,
    string Name,
    string? Description,
    int Quantity,
    int Price
);

public record CreateAbacateBillingRequest(
    string Frequency,
    string[] Methods,
    AbacateBillingProductRequest[] Products,
    string ReturnUrl,
    string CompletionUrl,
    string CustomerId
);

// ---------- Responses ----------

public record AbacateCustomerMetadata(
    string Name,
    string Cellphone,
    string Email,
    string TaxId
);

public record AbacateCustomerData(
    string Id,
    AbacateCustomerMetadata Metadata
);

public record AbacateCustomerResponse(
    AbacateCustomerData? Data,
    string? Error
);

public record AbacateBillingProductResponse(
    string Id,
    string ExternalId,
    int Quantity
);

public record AbacateBillingCustomerResponse(
    string Id,
    AbacateCustomerMetadata? Metadata
);

public record AbacateBillingData(
    string Id,
    string Url,
    long Amount,
    string Status,
    bool DevMode,
    string[] Methods,
    AbacateBillingProductResponse[]? Products,
    string Frequency,
    AbacateBillingCustomerResponse? Customer,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record AbacateBillingResponse(
    AbacateBillingData? Data,
    string? Error
);

// ---------- Webhook Payload ----------

public record WebhookBillingData(string Id, long Amount, string Status);

public record WebhookCheckoutData(long PaidAmount, long PlatformFee, string? ReceiptUrl);

public record WebhookPixPayerInfo(string? Name, string? TaxId);

public record WebhookCardPayerInfo(string? Number, string? Brand);

public record WebhookPayerInformation(WebhookPixPayerInfo? PIX, WebhookCardPayerInfo? CARD);

public record WebhookEventData(
    WebhookBillingData? Billing,
    WebhookCheckoutData? Checkout,
    WebhookPayerInformation? PayerInformation
);

public record WebhookPayload(string Event, int ApiVersion, bool DevMode, WebhookEventData? Data);
