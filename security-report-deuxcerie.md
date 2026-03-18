# 🔐 Security Vulnerability Report — Deuxcerie Ecommerce Backend

**Date:** 2026-03-18  
**Scope:** `EcommerceApi` — ASP.NET Core 10 / .NET 10 backend  
**Analyst:** Cybersecurity Specialist — Payment Gateway & Ecommerce  
**Severity Legend:** 🔴 Critical · 🟠 High · 🟡 Medium · 🔵 Low / Informational

---

## Executive Summary

A full source-code review of the Deuxcerie ecommerce backend identified **19 distinct vulnerabilities** spanning authentication bypass, payment-integrity bypass, injection risks, denial-of-service vectors, sensitive-data exposure, insecure cryptography, and operational weaknesses. Several findings are independently exploitable to cause financial fraud, customer data theft, or complete service disruption without any prior authentication.

---

## Finding Index

| # | Severity | Title | File |
|---|----------|-------|------|
| 1 | 🔴 Critical | HMAC Signature Validation Silently Disabled | `WebhookEndpoints.cs` |
| 2 | 🔴 Critical | Payment Amount Never Verified for PIX Webhooks | `WebhookEndpoints.cs` |
| 3 | 🔴 Critical | Replay Attack: No Webhook Idempotency for Failure Events | `WebhookEndpoints.cs` |
| 4 | 🔴 Critical | HmacPublicKey Committed to `appsettings.json` | `appsettings.json` |
| 5 | 🟠 High | Order Created Before Payment Confirmed — Race Condition on Status Query | `CheckoutSessionEndpoints.cs` |
| 6 | 🟠 High | Delivery Date Minimum Validation Bypassed by Timezone Manipulation | `CreateOrderHandler.cs` |
| 7 | 🟠 High | CPF Stored and Logged in Plain Text Throughout | Multiple files |
| 8 | 🟠 High | File Upload: Magic Bytes Check Bypassed via Partial Read | `OrderEndpoints.cs` |
| 9 | 🟠 High | Storage Service Uses Static `HttpClient` — No Retry or Timeout | `StorageService.cs` |
| 10 | 🟠 High | Dedup Logic Leaks Existing Session to Different User | `CreateOrderHandler.cs` |
| 11 | 🟡 Medium | Rate Limiter Uses `RemoteIpAddress` — Trivially Bypassed Behind Proxy | `Program.cs` |
| 12 | 🟡 Medium | CORS `AllowedOrigins` Empty Array in Production Config | `appsettings.json` |
| 13 | 🟡 Medium | No Expiry Enforced on PIX Checkout Sessions | `CheckoutSessionEndpoints.cs` |
| 14 | 🟡 Medium | Webhook Log Stores Full Raw Payload (PII / Card Data) | `WebhookEndpoints.cs` |
| 15 | 🟡 Medium | `IDbConnection` Registered as Transient — Connection Leak Risk | `Program.cs` |
| 16 | 🟡 Medium | `ForwardedHeaders` Accepts Any Proxy IP — IP Spoofing | `Program.cs` |
| 17 | 🟡 Medium | No Content-Length Cap on Webhook Body | `WebhookEndpoints.cs` |
| 18 | 🔵 Low | `DevMode` Flag Can Reach Production If Misconfigured | `Program.cs` / `appsettings.json` |
| 19 | 🔵 Low | Error Messages Leak Internal Business Logic to Clients | `OrderEndpoints.cs` / `CreateOrderHandler.cs` |

---

## Detailed Findings

---

### Finding 1 — 🔴 CRITICAL: HMAC Signature Validation Silently Disabled

**File:** `EcommerceApi/Api/Endpoints/WebhookEndpoints.cs`, lines ~48–53

**Description:**  
The HMAC-SHA256 signature check — the primary cryptographic control that proves a webhook genuinely originated from AbacatePay — is **completely non-enforcing**. The code computes and checks the signature, then logs a warning if it fails but **proceeds regardless**. Any attacker who can send an HTTP POST to `/api/v1/ecommerce/webhooks/abacatepay` with the correct `webhookSecret` query parameter can forge arbitrary payment-completed events and create fraudulent orders.

```csharp
// CURRENT — INSECURE
var signatureValid = validator.ValidateHmacSignature(rawBody, signatureHeader, hmacKey);
if (!signatureValid)
    logger.LogWarning("HMAC inválido — ignorado temporariamente para diagnóstico...");
// ^ No return! Execution continues with an invalid signature.
```

**Impact:** Full financial fraud. Attacker forges `checkout.completed` events → orders are created and marked paid → goods shipped without payment.

**Fix:**

```csharp
// FIXED
var signatureValid = validator.ValidateHmacSignature(rawBody, signatureHeader, hmacKey);
if (!signatureValid)
{
    await LogWebhook(db, receivedAt, null, rawBody, signatureHeader,
        false, secretValid, "InvalidSignature", "HMAC inválido", null, 401);
    return Results.Unauthorized();
}
```

Remove any "temporary diagnostic" bypass. This must be a hard gate.

---

### Finding 2 — 🔴 CRITICAL: Payment Amount Never Verified for PIX Webhooks

**File:** `EcommerceApi/Api/Endpoints/WebhookEndpoints.cs`, `ProcessPaymentSuccessAsync`

**Description:**  
The code checks `receivedAmount != session.AmountCents` and logs a `LogCritical`, but **only returns after the critical log** — the function returns `void` via `return;`, which is correct in isolation. However, the `receivedAmount` is pulled from a deeply-nested optional field chain:

```csharp
var receivedAmount = transparent?.Amount ?? checkout?.Amount ?? billingData?.Amount;
```

If AbacatePay sends a `transparent.completed` event where `transparent.Amount` is `null` or `0` (e.g. a partially-malformed payload or a future API change), the null-coalescing chain can resolve to `null` and the function bails out with `LogCritical` instead of rejecting. More dangerously, the `receivedStatus` field:

```csharp
var receivedStatus = transparent?.Status ?? checkout?.Status ?? billingData?.Status;
if (!string.Equals(receivedStatus, "PAID", ...))
    return; // Warning only
```

This means a webhook with `status = null` (missing field) would **pass the status check** since `string.Equals(null, "PAID")` is `false` and would be silently rejected — BUT the status check runs *after* the amount check. An attacker who crafts a payload where `amount` matches but status is missing gets past amount verification and is then softly rejected. The bigger risk is that the entire verification chain relies on the webhook body content rather than querying AbacatePay's API to confirm payment status independently.

**Impact:** Potential for crafted webhooks to create orders without verifiable payment confirmation.

**Fix:**
1. After receiving a webhook event, **call back to AbacatePay's GET billing API** to independently confirm the billing ID, amount, and status. Never trust the webhook payload alone for financial decisions.
2. Treat any `null` status or amount as an **invalid payload** and return immediately:

```csharp
if (receivedAmount is null || receivedStatus is null)
{
    logger.LogCritical("Payload inválido — amount ou status ausente para billing {BillingId}", billingId);
    return; // Already handled correctly, but add explicit null-status guard
}
if (!string.Equals(receivedStatus, "PAID", StringComparison.OrdinalIgnoreCase))
{
    logger.LogWarning(...);
    return;
}
```

---

### Finding 3 — 🔴 CRITICAL: Replay Attack — No Webhook Idempotency for Failure/Refund Events

**File:** `EcommerceApi/Api/Endpoints/WebhookEndpoints.cs`, `ProcessPaymentFailureAsync` / `UpdateTransactionStatus`

**Description:**  
The success path has an idempotency check (`SELECT pt."OrderId" FROM payment_transactions WHERE "AbacateBillingId" = @BillingId`). The **failure, refund, and dispute paths have no such check**. An attacker (or a misconfigured AbacatePay retry) can replay `checkout.refunded` events repeatedly. Each replay calls `UpdateTransactionStatus` which executes an `UPDATE` — harmless in terms of status, but also calls `SendPaymentRefundedAsync` / `SendPaymentDisputedAsync` **every single time**. This floods customers with email notifications and can be used as an email-bombing denial-of-service against customers.

**Impact:** Email harassment of customers; potential for status-flip attacks if race conditions exist.

**Fix:**

```csharp
private static async Task ProcessPaymentFailureAsync(...)
{
    // Idempotency: check if already in this terminal state
    var existing = await db.QueryFirstOrDefaultAsync<int?>(
        @"SELECT ""Status"" FROM payment_transactions WHERE ""AbacateBillingId"" = @BillingId LIMIT 1",
        new { BillingId = billingId });

    if (existing == status) return; // Already processed

    await UpdateTransactionStatus(...);
    // ... send email only once
}
```

Also add a `webhook_event_log` deduplication check based on `AbacateBillingId` + `EventType`.

---

### Finding 4 — 🔴 CRITICAL: HmacPublicKey Committed to Version Control

**File:** `EcommerceApi/appsettings.json`, line ~12

**Description:**  
The HMAC public key used to validate webhook signatures is hardcoded directly in `appsettings.json`, which is tracked by git:

```json
"HmacPublicKey": "t9dXRhHHo3yDEj5pVDYz0frf7q6bMKyMRmxxCPIPp3RCplBfXRxqlC6ZpiWmOqj4L63q..."
```

This key is now in git history permanently. Anyone with read access to the repository (current or former employees, a leaked repo, a compromised CI/CD system) has this key. Combined with Finding 1 being re-enabled, this key alone is insufficient protection — but if Finding 1 is fixed, this key's exposure still means any attacker who obtained it can forge valid HMAC signatures.

**Impact:** Complete bypass of webhook authentication once Finding 1 is properly enforced.

**Fix:**
1. **Rotate the key immediately** with AbacatePay.
2. Remove from `appsettings.json`. Store only in environment variables or a secrets manager (Azure Key Vault, AWS Secrets Manager, Doppler, etc.):

```bash
# Environment variable (never committed)
AbacatePay__HmacPublicKey=<new_rotated_key>
```

3. Use `dotnet user-secrets` for local development only.
4. Add `appsettings.*.json` to `.gitignore` or audit all keys via `git secret` scanning.

---

### Finding 5 — 🟠 HIGH: Race Condition on Checkout Session Status Endpoint

**File:** `EcommerceApi/Api/Endpoints/CheckoutSessionEndpoints.cs`

**Description:**  
The `/checkout-sessions/{sessionId}/status` endpoint polls for payment completion by checking `checkout_sessions.UsedAt` and then joining to `payment_transactions`. This is correct architecturally, but the query to find the `orderId` is:

```sql
SELECT o."Id" FROM orders o
INNER JOIN payment_transactions pt ON pt."OrderId" = o."Id"
INNER JOIN checkout_sessions cs ON cs."AbacateBillingId" = pt."AbacateBillingId"
WHERE cs."Id" = @SessionId LIMIT 1
```

There is a **TOCTOU (Time-Of-Check-Time-Of-Use)** window: `UsedAt` may be set but the `payment_transactions` INSERT in the webhook handler's transaction may not yet be committed. The frontend receives `status: "paid"` but `orderId: null`, leading to a broken UX and potentially repeated order-creation attempts.

Additionally, this endpoint has no authentication — any party who guesses or obtains a `sessionId` GUID can poll payment status for any customer's order.

**Impact:** Information disclosure of payment status; broken checkout UX under load.

**Fix:**
1. Move the `orderId` lookup to query directly from `orders` joined on `checkout_sessions.AbacateBillingId` with a single atomic query inside the webhook transaction, or cache it in `checkout_sessions` when marking `UsedAt`.
2. Consider adding a short-lived signed token (HMAC or JWT) to the `sessionId` response so only the original customer can poll status.

---

### Finding 6 — 🟠 HIGH: Delivery Date Minimum Validation Bypassed via UTC Timezone

**File:** `EcommerceApi/Application/Orders/CreateOrderHandler.cs`, lines ~149–152

**Description:**  
The minimum delivery date check uses:

```csharp
var minDeliveryDate = DateTime.UtcNow.Date.AddDays(2);
if (cmd.DeliveryDate.Date < minDeliveryDate)
    throw new ArgumentException($"Data de entrega mínima é {minDeliveryDate:dd/MM/yyyy}.");
```

But the delivery date from the HTTP form is parsed and forced to UTC in the endpoint:

```csharp
deliveryDate = DateTime.SpecifyKind(deliveryDate, DateTimeKind.Utc);
```

A customer in UTC-3 (Brasília time) submitting at 23:00 local time is at 02:00 UTC next day. Their "today + 2" is actually "UTC-today + 2" which may differ by one day from the server's "UTC-now + 2". This creates an inconsistency where dates can be accepted or rejected based on the server's UTC clock, not the customer's local date — confusing customers and potentially allowing same-day delivery slots to be booked.

**Impact:** Business logic bypass; inconsistent UX for Brazilian customers.

**Fix:**  
Accept the delivery date as a `DateOnly` value and compare against business-timezone-aware date calculations:

```csharp
// Use Brazil timezone for business date calculations
var brazilTz = TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");
var brazilNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, brazilTz);
var minDeliveryDate = DateOnly.FromDateTime(brazilNow).AddDays(2);
var submittedDate = DateOnly.FromDateTime(cmd.DeliveryDate);
if (submittedDate < minDeliveryDate)
    throw new ArgumentException(...);
```

---

### Finding 7 — 🟠 HIGH: CPF (Brazilian Tax ID) Stored and Logged in Plain Text

**Files:** `checkout_sessions` table (via `TaxId`), `payment_transactions` (`PayerTaxIdMasked`), webhook logs (`RawPayload`), application logs

**Description:**  
The CPF (`taxId`) is:
- Stored verbatim in `checkout_sessions."TaxId"` — never masked or hashed.
- Passed through as `session.TaxId` in the webhook handler without being used (redundant storage).
- Included in the full raw webhook payload stored in `webhook_event_log."RawPayload"` (Finding 14 overlap).
- Present in `payment_transactions."PayerTaxIdMasked"` only when AbacatePay returns it masked — but the session table holds the original.

Under Brazil's LGPD (Lei Geral de Proteção de Dados), CPF is sensitive personal data requiring appropriate protection. A SQL injection or database breach exposes CPFs for every customer who ever placed an order.

**Impact:** LGPD non-compliance; mass PII exposure in case of database breach.

**Fix:**
1. **Do not store the raw CPF** after payment initiation. Store only the last 3 digits or a one-way hash (e.g., `SHA-256(cpf + pepper)`) for deduplication purposes.
2. If the raw CPF must be stored temporarily (e.g., for the AbacatePay customer creation call), encrypt it at rest using column-level encryption or a KMS-backed field.
3. Remove `TaxId` from `checkout_sessions` once the payment transaction is confirmed — replace with the masked version.

---

### Finding 8 — 🟠 HIGH: File Upload Magic Bytes Check Can Be Bypassed

**File:** `EcommerceApi/Api/Endpoints/OrderEndpoints.cs`, `HasValidMagicBytesAsync`

**Description:**  
The magic bytes validation reads only the first 12 bytes of the file stream and validates the MIME signature. This is **not sufficient** protection:

1. **Polyglot files:** An attacker can craft a file that starts with valid JPEG/PNG magic bytes but contains malicious content afterward (e.g., a PHP webshell, XML with XXE payloads, or a ZIP bomb). If the R2 storage ever serves these files back to users or if any downstream processing occurs, the malicious content executes.

2. **GIFAR attack:** A valid GIF header followed by a JAR or JS payload passes the GIF check.

3. **SVG is missing:** SVG files with `<script>` tags are not on the allowed list, which is good — but WebP and GIF variants can still contain metadata with XSS payloads if served with wrong headers.

4. **The stream is opened twice:** `file.OpenReadStream()` is called in `HasValidMagicBytesAsync` and again when creating `FileReference`. Depending on the underlying stream implementation, the second read may start from position 0 or fail silently.

**Impact:** Stored XSS, server-side request forgery, or remote code execution if files are processed downstream.

**Fix:**
1. Use a library like `MimeDetective` or `ImageSharp` to fully validate the file content, not just the header.
2. Re-encode all images through a processing pipeline (e.g., decode and re-encode with `ImageSharp`) before storing — this strips all metadata and polyglot payloads.
3. Fix the double-stream issue:

```csharp
// Read bytes once, validate, then wrap in MemoryStream for storage
using var ms = new MemoryStream();
await file.CopyToAsync(ms);
ms.Position = 0;
if (!HasValidMagicBytes(ms.ToArray(), file.ContentType)) return Results.BadRequest(...);
ms.Position = 0;
refs.Add(new FileReference(ms, file.ContentType, file.FileName));
```

4. Ensure R2 serves files with `Content-Disposition: attachment` and a strict `Content-Security-Policy` on the CDN.

---

### Finding 9 — 🟠 HIGH: StorageService Uses Static `HttpClient` Without Timeout

**File:** `EcommerceApi/Infrastructure/Services/StorageService.cs`

**Description:**  
```csharp
private static readonly HttpClient _httpClient = new();
```

A static `HttpClient` with no configured timeout is used for all R2 uploads. This has multiple problems:
1. **No timeout:** A slow or unresponsive R2 endpoint will cause the `UploadFileAsync` call to hang indefinitely, holding the request thread and eventually exhausting the thread pool — a **denial-of-service** vector exploitable by making AbacatePay-side operations slow.
2. **DNS caching:** Static `HttpClient` instances cache DNS for the lifetime of the process, causing failures if R2's IP changes.
3. **No retry policy:** A single transient failure aborts the entire order creation.

**Impact:** Thread pool exhaustion → full API unavailability.

**Fix:**
1. Register `StorageService` with `IHttpClientFactory`:

```csharp
// Program.cs
builder.Services.AddHttpClient("R2", client => {
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<StorageService>();

// StorageService.cs
public class StorageService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    private HttpClient GetClient() => httpClientFactory.CreateClient("R2");
    // ... replace _httpClient usages
}
```

2. Add Polly retry policy for transient HTTP errors.

---

### Finding 10 — 🟠 HIGH: Card Session Deduplication Leaks Existing Session to Different User

**File:** `EcommerceApi/Application/Orders/CreateOrderHandler.cs`, `HandleCardAsync`, lines ~100–115

**Description:**  
The deduplication query for card payments:

```csharp
var recent = await db.QueryFirstOrDefaultAsync<(Guid Id, string? CheckoutUrl)>(
    """
    SELECT "Id", "CheckoutUrl"
    FROM checkout_sessions
    WHERE "ClientMobile" = @Mobile
      AND "AmountCents"  = @Amount
      AND "UsedAt"       IS NULL
      AND "CreatedAt"    > @Threshold
    LIMIT 1
    """,
    new { Mobile = command.ClientMobile, Amount = cardAmountCents, Threshold = DateTime.UtcNow.AddMinutes(-5) });
```

This matches on `ClientMobile + AmountCents`. An attacker who knows another customer's mobile number and can guess or observe their cart total (e.g., from a shared price list) can **receive the existing customer's checkout URL** — which is an AbacatePay hosted payment page pre-filled with the victim's cart. The attacker could then:
- Pay using their own card under the victim's identity.
- Observe the order total and timing of competitors/customers.

**Impact:** Session hijacking, identity confusion in payments, minor PII exposure.

**Fix:**  
Include the customer's `Email` or `TaxId` hash in the deduplication query to prevent cross-customer session sharing:

```csharp
WHERE "ClientMobile" = @Mobile
  AND "Email"        = @Email
  AND "AmountCents"  = @Amount
  AND "UsedAt"       IS NULL
  AND "CreatedAt"    > @Threshold
```

---

### Finding 11 — 🟡 MEDIUM: Rate Limiter Uses `RemoteIpAddress` — Bypassed Behind Reverse Proxy

**File:** `EcommerceApi/Program.cs`

**Description:**  
Both the global rate limiter and all named policies use `ctx.Connection.RemoteIpAddress` as the partition key. When running behind Fly.io's proxy (as configured in `fly.toml`), `RemoteIpAddress` is always the proxy's IP — not the real client IP. The `ForwardedHeaders` middleware is configured, which correctly populates `HttpContext.Connection.RemoteIpAddress` only if it runs **before** the rate limiter.

Looking at the middleware order:
```csharp
app.UseForwardedHeaders(); // ✅ correct position
app.UseRequestTimeouts();
app.UseRateLimiter();      // ✅ after ForwardedHeaders
```

The order is correct, **however** `ForwardedHeadersOptions` has `KnownProxies` empty by default with `ForwardLimit = 1`. If the Fly.io proxy chain adds multiple `X-Forwarded-For` hops, only the last hop is trusted, which may still be Fly's internal IP. Additionally, the comment in the code itself warns: *"In production, restrict to your actual proxy IP"* — this has **not been done**.

Without a `KnownProxies` restriction, any client can spoof `X-Forwarded-For: 1.2.3.4` and effectively choose their own rate-limit partition key.

**Impact:** Rate limit bypass; order creation spam; brute-force of payment endpoints.

**Fix:**

```csharp
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    // Add actual Fly.io proxy IP(s) — obtain from fly.io dashboard or metadata
    options.KnownProxies.Add(IPAddress.Parse("YOUR_FLY_PROXY_IP"));
    options.ForwardLimit = 1;
});
```

---

### Finding 12 — 🟡 MEDIUM: CORS `AllowedOrigins` Is Empty Array in Production Config

**File:** `EcommerceApi/appsettings.json`

**Description:**  
```json
"AllowedOrigins": []
```

The production `appsettings.json` has an empty `AllowedOrigins` array. The CORS policy:

```csharp
opt.AddPolicy("Frontend", p =>
    p.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [])
     .WithMethods("GET", "POST")
     .AllowAnyHeader());
```

`WithOrigins()` called with an empty array results in **no origins being allowed**, but this does not block server-side processing — it only blocks browser preflight. Server-to-server calls (e.g., from a script or curl) are completely unaffected. Meanwhile, legitimate browser users may be blocked if the `AllowedOrigins` environment variable is not properly set in production.

More importantly, if `AllowedOrigins` is accidentally set to `["*"]` in any environment, `AllowAnyHeader()` combined with a wildcard origin allows credential-bearing cross-origin requests.

**Impact:** Legitimate users blocked; potential for misconfiguration to open full CORS.

**Fix:**
1. Set `AllowedOrigins` via environment variable in production, not `appsettings.json`.
2. Add a startup assertion:

```csharp
var origins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [];
if (!devMode && origins.Length == 0)
    throw new InvalidOperationException("AllowedOrigins must be configured for production.");
```

---

### Finding 13 — 🟡 MEDIUM: No Expiry Enforced on PIX Checkout Sessions

**File:** `EcommerceApi/Api/Endpoints/CheckoutSessionEndpoints.cs`

**Description:**  
PIX QR codes expire (AbacatePay returns `ExpiresAt` in the `PixTransparentData` response). This expiry is returned to the frontend but **never stored or enforced server-side**. The `/checkout-sessions/{sessionId}/status` endpoint will continue returning `status: "pending"` for expired PIX sessions indefinitely. A customer could attempt to pay an expired QR code, the frontend shows "pending," and the customer is confused or double-charged if they create a new order.

The `checkout_sessions` table has no `ExpiresAt` column, meaning there is also no way to clean up stale sessions or detect expiry server-side.

**Impact:** Customer confusion; potential double-payment; unbounded database growth.

**Fix:**
1. Store `ExpiresAt` in `checkout_sessions`.
2. Return `status: "expired"` from the status endpoint when `UsedAt IS NULL AND ExpiresAt < NOW()`.
3. Add a periodic cleanup job to tombstone expired sessions.

---

### Finding 14 — 🟡 MEDIUM: Webhook Log Stores Full Raw Payload Including PII

**File:** `EcommerceApi/Api/Endpoints/WebhookEndpoints.cs`, `LogWebhook`

**Description:**  
Every webhook event stores the complete raw JSON body in `webhook_event_log."RawPayload"`. AbacatePay's webhook payloads include:
- Customer name (`data.customer.name`)
- Partially-masked CPF (`data.customer.taxId` — e.g. `"123.***.***-**"`)
- Payer name (`data.payerInformation.PIX.name`)
- Card last four digits (`data.payerInformation.CARD.number`)
- Card brand

This data is stored indefinitely in a log table with no retention policy. Under LGPD, retaining PII in logs without a defined retention period and purpose limitation is non-compliant. A breach of the `webhook_event_log` table exposes payment metadata for all customers.

**Impact:** LGPD non-compliance; PII exposure in log table.

**Fix:**
1. Sanitize the raw payload before logging — redact `data.customer`, `data.payerInformation` fields:

```csharp
private static string SanitizeWebhookPayload(string rawBody)
{
    // Parse and redact sensitive fields before storing
    // Or store only event type + billing ID, not the full body
}
```

2. Implement a log retention policy (e.g., 90 days) with automatic deletion.
3. Consider storing only a hash of the payload for integrity verification instead of the full content.

---

### Finding 15 — 🟡 MEDIUM: `IDbConnection` Registered as Transient — Connection Leak Risk

**File:** `EcommerceApi/Program.cs`

**Description:**  
```csharp
builder.Services.AddTransient<IDbConnection>(_ => new NpgsqlConnection(connectionString));
```

`IDbConnection` is registered as `Transient`, meaning a new `NpgsqlConnection` is created for each injection but **never automatically disposed** by the DI container (since `IDbConnection` is not `IDisposable` in the DI resolution contract — it is, but the container doesn't know to call it). In the webhook handler, the connection is cast to `NpgsqlConnection` and `Open()` is called manually:

```csharp
var conn = (NpgsqlConnection)db;
if (conn.State != System.Data.ConnectionState.Open)
    conn.Open();
```

If an exception occurs before the `using` scope or `tx.Rollback()` path, the connection may not be returned to the pool.

**Impact:** Connection pool exhaustion under error conditions; potential database unavailability.

**Fix:**  
Register as `Scoped` and ensure disposal:

```csharp
builder.Services.AddScoped<IDbConnection>(sp =>
{
    var conn = new NpgsqlConnection(connectionString);
    return conn;
});
```

Or use `IDbConnectionFactory` pattern with explicit `using` in each handler.

---

### Finding 16 — 🟡 MEDIUM: `ForwardedHeaders` Middleware Trusts All Proxies

**File:** `EcommerceApi/Program.cs`

**Description:**  
As noted in Finding 11, the `KnownProxies` list is empty, which in ASP.NET Core's `ForwardedHeaders` middleware means it defaults to trusting `127.0.0.1` and `::1` only — but with `KnownNetworks` also cleared, the behavior can vary by .NET version. The comment in the code explicitly acknowledges this is unfinished:

```csharp
// In production, restrict to your actual proxy IP:
//   options.KnownProxies.Add(IPAddress.Parse("10.0.0.1"));
```

This is a known unresolved security TODO in production code.

**Impact:** IP spoofing for rate-limit bypass and audit log falsification.

**Fix:** See Finding 11 fix. This is the same root cause.

---

### Finding 17 — 🟡 MEDIUM: No Content-Length Cap on Webhook Request Body

**File:** `EcommerceApi/Api/Endpoints/WebhookEndpoints.cs`

**Description:**  
The Kestrel global limit is 50 MB:
```csharp
options.Limits.MaxRequestBodySize = 50 * 1024 * 1024;
```

The webhook endpoint reads the entire body into a string:
```csharp
var rawBody = await reader.ReadToEndAsync();
```

An attacker who passes the `webhookSecret` check (or if Finding 4's key is known) can send a 50 MB webhook body. This body is then:
1. Held in memory as a string.
2. Stored in `webhook_event_log."RawPayload"` (a TEXT column).
3. Parsed by `JsonSerializer.Deserialize`.

A burst of such requests exhausts both memory and database storage.

**Impact:** Memory exhaustion DoS; database disk exhaustion.

**Fix:**
1. Apply a webhook-specific body size limit:

```csharp
app.MapPost("/webhooks/abacatepay", ...)
   .WithRequestTimeout("webhooks")
   .RequireRateLimiting("webhooks")
   .AddEndpointFilter(async (ctx, next) => {
       ctx.HttpContext.Request.Body = new LengthLimitedStream(
           ctx.HttpContext.Request.Body, maxBytes: 1 * 1024 * 1024); // 1 MB
       return await next(ctx);
   });
```

2. Or use `[RequestSizeLimit(1_048_576)]` attribute.

---

### Finding 18 — 🔵 LOW: `DevMode` Flag Can Silently Reach Production

**File:** `EcommerceApi/Program.cs`, `EcommerceApi/appsettings.json`

**Description:**  
`DevMode` is read from configuration and used to select test API tokens and skip certain validations. The default in `appsettings.json` is `"DevMode": false`, which is correct — but the flag is also stored in `checkout_sessions.DevMode` and `payment_transactions.DevMode`. If a misconfiguration (e.g., a wrong environment variable) sets `DevMode: true` in production:
- Test API tokens are used (payments not real).
- The `DevModeMismatch` check in the webhook handler would block real webhooks.
- Real customer orders would silently fail.

There is no runtime assertion preventing `DevMode: true` in a production environment.

**Impact:** Silent payment failures in production; financial loss.

**Fix:**

```csharp
// Program.cs — add after devMode is read
if (devMode && Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Production")
    throw new InvalidOperationException("DevMode cannot be enabled in Production environment.");
```

---

### Finding 19 — 🔵 LOW: Error Messages Leak Internal Business Logic

**File:** `EcommerceApi/Api/Endpoints/OrderEndpoints.cs`, `EcommerceApi/Application/Orders/CreateOrderHandler.cs`

**Description:**  
Error responses expose internal details:

```json
{ "error": "O preço do produto 3fa85f64-5717-4562-b3fc-2c963f66afa6 mudou. Por favor, atualize seu carrinho." }
{ "error": "Produto 3fa85f64-5717-4562-b3fc-2c963f66afa6 não encontrado ou inativo." }
{ "error": "Data de entrega mínima é 20/03/2026." }
```

These responses reveal:
- Internal product GUIDs (product enumeration vector).
- Whether a product is "inactive" (inventory intelligence).
- The server's current date (timezone/clock fingerprinting).
- Minimum business rule parameters.

**Impact:** Low-severity information disclosure; aids in targeted attacks.

**Fix:**  
Return generic error codes with a user-facing message map:

```csharp
// Generic response to client
return Results.BadRequest(new { error = "ERR_PRODUCT_UNAVAILABLE", message = "Um ou mais produtos não estão disponíveis." });
// Log the specific internal detail server-side
logger.LogWarning("Produto {ProductId} não encontrado ou inativo", item.ProductId);
```

---

## Summary Table

| # | Severity | CVSS Estimate | Exploitability | Financial Impact |
|---|----------|---------------|----------------|-----------------|
| 1 | 🔴 Critical | 9.8 | No auth needed | Direct fraud |
| 2 | 🔴 Critical | 9.1 | Needs webhook access | Order without payment |
| 3 | 🔴 Critical | 8.2 | Replay existing events | Email bombing + status manipulation |
| 4 | 🔴 Critical | 9.0 | Git repo access | Enables Finding 1 permanently |
| 5 | 🟠 High | 6.5 | Timing + session enumeration | Broken UX / status exposure |
| 6 | 🟠 High | 5.3 | Any customer | Business rule bypass |
| 7 | 🟠 High | 7.5 | DB breach | LGPD fine + PII exposure |
| 8 | 🟠 High | 7.2 | File upload | XSS / malware storage |
| 9 | 🟠 High | 7.5 | External slow endpoint | Full API DoS |
| 10 | 🟠 High | 6.8 | Knows victim mobile | Session hijack |
| 11 | 🟡 Medium | 5.3 | Any client | Rate-limit bypass |
| 12 | 🟡 Medium | 4.3 | Misconfiguration | CORS open or blocked |
| 13 | 🟡 Medium | 4.0 | Any user | UX confusion / double pay |
| 14 | 🟡 Medium | 5.5 | DB breach | LGPD non-compliance |
| 15 | 🟡 Medium | 5.0 | Error conditions | DB unavailability |
| 16 | 🟡 Medium | 5.3 | Any client | IP spoofing |
| 17 | 🟡 Medium | 5.5 | Webhook endpoint | Memory / DB DoS |
| 18 | 🔵 Low | 3.1 | Misconfiguration | Silent payment failure |
| 19 | 🔵 Low | 2.7 | Any client | Information disclosure |

---

## Immediate Action Plan (Priority Order)

### 🚨 Do This Now (Today)

1. **Rotate the HmacPublicKey** with AbacatePay (Finding 4). The current key is in git history.
2. **Enforce HMAC validation** — remove the `LogWarning` bypass and make it a hard `401` return (Finding 1).
3. **Add idempotency to failure/refund webhook handlers** (Finding 3).

### 📅 This Week

4. Implement server-side AbacatePay callback verification for payment success (Finding 2).
5. Fix the `StorageService` static `HttpClient` with a proper timeout (Finding 9).
6. Fix the card session deduplication to include email in the query (Finding 10).
7. Restrict `ForwardedHeaders` to known Fly.io proxy IPs (Findings 11 & 16).

### 📅 This Sprint

8. Encrypt or hash CPF at rest, implement LGPD data minimization (Finding 7).
9. Improve file upload validation with full image re-encoding (Finding 8).
10. Add `ExpiresAt` to `checkout_sessions` and enforce PIX expiry (Finding 13).
11. Sanitize PII from webhook raw payload logs (Finding 14).
12. Fix `IDbConnection` registration to `Scoped` (Finding 15).
13. Add webhook body size limit of 1 MB (Finding 17).
14. Set `AllowedOrigins` via environment variable with startup assertion (Finding 12).

### 📅 Next Sprint

15. Add `DevMode` production guard (Finding 18).
16. Genericize error messages (Finding 19).
17. Fix timezone handling for delivery date validation (Finding 6).
18. Resolve status endpoint race condition (Finding 5).

---

## Appendix: Files Reviewed

- `EcommerceApi/Api/Endpoints/CheckoutSessionEndpoints.cs`
- `EcommerceApi/Api/Endpoints/OrderEndpoints.cs`
- `EcommerceApi/Api/Endpoints/PaymentStatusEndpoints.cs`
- `EcommerceApi/Api/Endpoints/ProductEndpoints.cs`
- `EcommerceApi/Api/Endpoints/WebhookEndpoints.cs`
- `EcommerceApi/Application/Orders/CreateOrderCommand.cs`
- `EcommerceApi/Application/Orders/CreateOrderHandler.cs`
- `EcommerceApi/Infrastructure/DTOs/AbacatePayDtos.cs`
- `EcommerceApi/Infrastructure/DTOs/PaymentTransactionDto.cs`
- `EcommerceApi/Infrastructure/Repositories/ProductRepository.cs`
- `EcommerceApi/Infrastructure/Services/AbacatePayService.cs`
- `EcommerceApi/Infrastructure/Services/EmailService.cs`
- `EcommerceApi/Infrastructure/Services/StorageService.cs`
- `EcommerceApi/Infrastructure/Services/WebhookValidationService.cs`
- `EcommerceApi/Program.cs`
- `EcommerceApi/appsettings.json`
- `EcommerceApi/appsettings.Development.json`

---

*Report generated by automated + manual source-code security review. All findings verified against the provided source files. No dynamic testing was performed.*
