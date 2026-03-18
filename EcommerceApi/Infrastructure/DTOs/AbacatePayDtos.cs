namespace EcommerceApi.Infrastructure.DTOs;

// ---------- Requests ----------

public record CreateAbacateCustomerRequest(
    string Name,
    string Cellphone,
    string Email,
    string TaxId
);

public record CreateAbacateProductRequest(
    string ExternalId,
    string Name,
    int Price,
    string Currency,
    string? Description,
    string? ImageUrl
);

public record AbacateCheckoutItem(string Id, int Quantity);

public record CreateAbacateCheckoutRequest(
    AbacateCheckoutItem[] Items,
    string[] Methods,
    string? CustomerId,
    string? ReturnUrl,
    string? CompletionUrl,
    string? ExternalId
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

public record AbacateProductData(
    string Id,
    string ExternalId,
    string Name,
    int Price,
    string Status,
    bool DevMode
);

public record AbacateProductResponse(AbacateProductData? Data, string? Error);

public record AbacateCheckoutData(
    string Id,
    string Url,
    long Amount,
    long? PaidAmount,
    string Status,
    bool DevMode
);

public record AbacateCheckoutResponse(AbacateCheckoutData? Data, string? Error);

// ---------- Webhook Payload ----------
// Payload real (checkout.completed):
// {
//   "event": "checkout.completed",
//   "apiVersion": 2,
//   "devMode": false,
//   "data": {
//     "checkout": { "id": "bill_xxx", "amount": 10000, "paidAmount": 10000, "platformFee": 80, "status": "PAID", ... },
//     "customer": { "id": "cust_xxx", "name": "...", "email": "...", "taxId": "123.***.***-**" },
//     "payerInformation": { "method": "PIX", "PIX": { "name": "...", "isSameAsCustomer": true } }
//                      OR  { "method": "CARD", "CARD": { "number": "1234", "brand": "Visa" } }
//   }
// }

public record WebhookCheckoutData(
    string? Id,           // bill_xxx — used as billingId to match checkout_sessions
    long Amount,
    long PaidAmount,
    long PlatformFee,
    string? ReceiptUrl,
    string? Status
);

public record WebhookCustomerData(string? Id, string? Name, string? Email, string? TaxId);

public record WebhookPixPayerInfo(string? Name, bool? IsSameAsCustomer);

public record WebhookCardPayerInfo(string? Number, string? Brand);

public record WebhookPayerInformation(string? Method, WebhookPixPayerInfo? PIX, WebhookCardPayerInfo? CARD);

// billing.paid uses data.billing.id; checkout.completed uses data.checkout.id
// We map both to handle whichever event AbacatePay actually fires
public record WebhookBillingData(
    string? Id,
    long Amount,
    long PaidAmount,
    long PlatformFee,
    string? ReceiptUrl,
    string? Status
);

public record WebhookPaymentData(long Amount, long Fee, string? Method);

public record WebhookEventData(
    WebhookCheckoutData? Checkout,
    WebhookBillingData? Billing,
    WebhookCustomerData? Customer,
    WebhookPayerInformation? PayerInformation,
    WebhookPaymentData? Payment
);

public record WebhookPayload(string Event, int ApiVersion, bool DevMode, WebhookEventData? Data);
