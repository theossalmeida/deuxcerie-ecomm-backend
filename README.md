# Deuxcerie Backend

REST API for order management and payment processing for the Deuxcerie fashion brand. Handles product catalog, order creation, file uploads for reference images, payment processing via PIX and credit card, and email notifications.

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Language | C# (.NET 10) |
| Framework | ASP.NET Core 10 (Minimal APIs) |
| Database | PostgreSQL + Dapper |
| Storage | Cloudflare R2 (S3-compatible) |
| Payment Gateway | AbacatePay (PIX & Credit Card) |
| Email | Resend API |
| Deployment | Fly.io (Docker) |
| Logging | Serilog (JSON structured) |

## Project Structure

```
EcommerceApi/
├── Api/Endpoints/          # Route definitions (products, orders, webhooks, status)
├── Application/Orders/     # Order creation command + handler (business logic)
├── Infrastructure/
│   ├── DTOs/               # External API models (AbacatePay, payments)
│   ├── Repositories/       # Data access (Dapper queries)
│   └── Services/           # External integrations (AbacatePay, email, storage)
├── Program.cs              # App bootstrap, DI, rate limiting, security headers
├── Dockerfile              # Multi-stage build
└── fly.toml                # Fly.io deployment config
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- PostgreSQL 14+
- Docker (optional, for containerized runs)
- Fly.io CLI (optional, for deployment)

## Getting Started

### 1. Clone and restore

```bash
git clone https://github.com/your-username/deuxcerie-backend.git
cd deuxcerie-backend
dotnet restore EcommerceApi/
```

### 2. Configure environment

The application reads configuration from environment variables or .NET User Secrets. For local development, use User Secrets:

```bash
dotnet user-secrets init --project EcommerceApi/
```

Then set each variable listed in the [Environment Variables](#environment-variables) section.

Alternatively, create an `appsettings.Local.json` file (excluded from git) or export variables in your shell.

### 3. Set up the database

Create a PostgreSQL database and apply the schema. The application uses raw SQL via Dapper — run the schema SQL manually or with your preferred migration tool before starting the API.

Required tables: `products`, `clients`, `orders`, `order_items`, `checkout_sessions`, `payment_transactions`, `webhook_event_log`.

### 4. Run

```bash
dotnet run --project EcommerceApi/
```

The API will be available at `http://localhost:5000`. Verify with:

```bash
curl http://localhost:5000/health
```

## Environment Variables

| Variable | Description |
|----------|-------------|
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string |
| `AbacatePay__DevMode` | `true` for sandbox, `false` for production |
| `AbacatePay__ApiToken` | Production API key |
| `AbacatePay__TestApiToken` | Sandbox API key |
| `AbacatePay__WebhookSecret` | Webhook signature secret (production) |
| `AbacatePay__TestWebhookSecret` | Webhook signature secret (sandbox) |
| `AbacatePay__RefundWebhookSecret` | Refund webhook secret (optional) |
| `AbacatePay__ApiBaseUrl` | AbacatePay base URL |
| `AbacatePay__ReturnUrl` | Redirect URL after card payment |
| `AbacatePay__CompletionUrl` | Redirect URL on checkout completion |
| `AllowedOrigins` | Semicolon-separated CORS allowed origins |
| `Resend__ApiToken` | Resend API key |
| `Resend__FromEmail` | Sender address (e.g. `Name <email@domain.com>`) |
| `R2__AccessKeyId` | Cloudflare R2 access key |
| `R2__SecretAccessKey` | Cloudflare R2 secret key |
| `R2__AccountId` | Cloudflare R2 account ID |
| `R2__BucketName` | R2 bucket name |
| `R2__PublicBaseUrl` | Public CDN URL for uploaded files |

> Never commit secrets. Use `.NET User Secrets`, environment variables, or a secret manager.

## Running with Docker

```bash
# Build
docker build -t deuxcerie-backend -f EcommerceApi/Dockerfile .

# Run (pass secrets as -e flags or use --env-file)
docker run -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="Host=...;Database=...;Username=...;Password=..." \
  -e AbacatePay__DevMode="true" \
  -e AbacatePay__TestApiToken="apk_test_..." \
  deuxcerie-backend
```

## API Reference

All routes are prefixed with `/api/v1/ecommerce`.

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/health` | Health check |
| `GET` | `/products` | List all active products |
| `GET` | `/products/{id}` | Get product by ID |
| `POST` | `/orders` | Create order and initiate payment |
| `GET` | `/orders/{orderId}/payment-status` | Poll payment status for an order |
| `GET` | `/checkout-sessions/{sessionId}/status` | Check if a checkout session was paid |
| `POST` | `/webhooks/abacatepay` | AbacatePay webhook receiver |

### POST /orders

Accepts `multipart/form-data`.

**Fields:**

| Field | Type | Description |
|-------|------|-------------|
| `clientName` | string | Full name (max 200 chars) |
| `clientMobile` | string | Brazilian mobile (10-11 digits) |
| `email` | string | Valid email address |
| `taxId` | string | CPF with checksum validation |
| `deliveryDate` | string | ISO date, minimum 2 days from now |
| `paymentMethod` | string | `PIX` or `CARD` |
| `items` | JSON string | Array of order items (see below) |
| `ref_{i}_{j}` | file | Reference images/PDFs (max 10 files, 10 MB each) |

**Items array:**
```json
[
  {
    "productId": "uuid",
    "quantity": 1,
    "paidPrice": 5000,
    "observation": "Optional note"
  }
]
```

**PIX response:**
```json
{
  "sessionId": "uuid",
  "paymentMethod": "PIX",
  "brCode": "00020126...",
  "brCodeBase64": "base64string",
  "expiresAt": "2026-03-27T18:00:00Z"
}
```

**Card response:**
```json
{
  "sessionId": "uuid",
  "paymentMethod": "CARD",
  "checkoutUrl": "https://..."
}
```

## Payment Flow

```
Client → POST /orders
  → Upload reference files to Cloudflare R2
  → Create payment on AbacatePay (PIX QR or card checkout link)
  → Store checkout session in DB
  → Return payment info to client

AbacatePay → POST /webhooks/abacatepay  (on payment confirmed)
  → Validate HMAC-SHA256 signature
  → Create order + order_items in DB
  → Record payment_transaction
  → Send confirmation email via Resend
```

## Rate Limiting

| Policy | Limit |
|--------|-------|
| Global (per IP) | 300 req/min |
| `/api` routes | 120 req/min |
| `POST /orders` | 100 req per 10 min |
| Payment status | 20 req/min |
| Webhooks | 50 req/min |

## Deployment (Fly.io)

The app is configured to deploy to Fly.io in the `gru` (Sao Paulo) region.

```bash
# Authenticate
flyctl auth login

# Deploy
flyctl deploy

# Set production secrets
flyctl secrets set AbacatePay__ApiToken=apk_live_...
flyctl secrets set ConnectionStrings__DefaultConnection="Host=..."

# View logs
flyctl logs

# Check status
flyctl status
```

Machine spec: 1 shared CPU, 512 MB RAM, minimum 1 machine always running.

## Security

- CPF checksum validation (11-digit Brazilian tax ID algorithm)
- Brazilian phone format validation
- File magic byte verification (rejects spoofed MIME types)
- Webhook HMAC-SHA256 signature validation (timing-safe comparison)
- CORS restricted to whitelisted origins
- Security headers: `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`
- Request body size limits (50 MB for orders, 1 MB for webhooks)
- Card payment deduplication (5-minute idempotency window by mobile + email + amount)

## External Services

- **[AbacatePay](https://abacatepay.com)** — payment processing (PIX & credit card)
- **[Resend](https://resend.com)** — transactional email
- **[Cloudflare R2](https://developers.cloudflare.com/r2/)** — object storage for reference files
- **[Fly.io](https://fly.io)** — application hosting
