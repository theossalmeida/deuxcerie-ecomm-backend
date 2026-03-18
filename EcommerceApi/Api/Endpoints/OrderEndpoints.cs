using EcommerceApi.Application.Orders;
using System.Net.Mail;
using System.Text.Json;

namespace EcommerceApi.Api.Endpoints;

public static class OrderEndpoints
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // Allowed MIME types for order reference images
    private static readonly HashSet<string> AllowedMimeTypes =
        ["image/jpeg", "image/png", "image/webp", "image/gif", "application/pdf"];

    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB per file
    private const int MaxFilesPerOrder = 10;

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/orders", async (HttpRequest request, CreateOrderHandler handler) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Content-Type deve ser multipart/form-data." });

            var form = await request.ReadFormAsync();

            // Trim all text inputs — prevents leading/trailing whitespace attacks
            var clientName    = form["clientName"].ToString().Trim();
            var clientMobile  = form["clientMobile"].ToString().Trim();
            var email         = form["email"].ToString().Trim();
            var taxId         = form["taxId"].ToString().Trim();
            var deliveryDateRaw = form["deliveryDate"].ToString().Trim();
            var itemsJson     = form["items"].ToString().Trim();

            // Required field presence
            if (string.IsNullOrWhiteSpace(clientName))
                return Results.BadRequest(new { error = "O campo 'clientName' é obrigatório." });
            if (string.IsNullOrWhiteSpace(clientMobile))
                return Results.BadRequest(new { error = "O campo 'clientMobile' é obrigatório." });
            if (string.IsNullOrWhiteSpace(email))
                return Results.BadRequest(new { error = "O campo 'email' é obrigatório." });
            if (string.IsNullOrWhiteSpace(taxId))
                return Results.BadRequest(new { error = "O campo 'taxId' (CPF) é obrigatório." });
            if (string.IsNullOrWhiteSpace(itemsJson))
                return Results.BadRequest(new { error = "O campo 'items' é obrigatório." });

            // Length caps — prevents oversized payloads reaching business logic
            if (clientName.Length > 200)
                return Results.BadRequest(new { error = "O campo 'clientName' excede 200 caracteres." });
            if (clientMobile.Length > 20)
                return Results.BadRequest(new { error = "O campo 'clientMobile' excede 20 caracteres." });
            if (email.Length > 200)
                return Results.BadRequest(new { error = "O campo 'email' excede 200 caracteres." });

            // Email format
            if (!IsValidEmail(email))
                return Results.BadRequest(new { error = "O campo 'email' contém um endereço inválido." });

            // Phone — Brazilian format (10 or 11 digits)
            if (!IsValidBrazilianPhone(clientMobile))
                return Results.BadRequest(new { error = "O campo 'clientMobile' deve ser um telefone brasileiro válido." });

            // CPF — 11-digit checksum (Brazilian standard)
            if (!IsValidCpf(taxId))
                return Results.BadRequest(new { error = "O campo 'taxId' deve ser um CPF válido com 11 dígitos." });

            // Items JSON
            List<OrderItemDto>? itemDtos;
            try
            {
                itemDtos = JsonSerializer.Deserialize<List<OrderItemDto>>(itemsJson, _jsonOptions);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "JSON inválido no campo 'items'." });
            }

            if (itemDtos is null || itemDtos.Count == 0)
                return Results.BadRequest(new { error = "O pedido deve ter pelo menos 1 item." });
            if (itemDtos.Count > 50)
                return Results.BadRequest(new { error = "O pedido não pode ter mais de 50 itens." });

            // Delivery date
            if (!DateTime.TryParse(deliveryDateRaw, out var deliveryDate))
                return Results.BadRequest(new { error = "deliveryDate inválido. Use formato ISO (ex: 2026-03-25)." });

            deliveryDate = DateTime.SpecifyKind(deliveryDate, DateTimeKind.Utc);

            // Validate total file count before touching any file
            if (form.Files.Count > MaxFilesPerOrder)
                return Results.BadRequest(new { error = $"O pedido não pode ter mais de {MaxFilesPerOrder} arquivos de referência." });

            // Build order items, validating each uploaded file
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

                    if (!AllowedMimeTypes.Contains(file.ContentType))
                        return Results.BadRequest(new
                        {
                            error = $"Tipo de arquivo não permitido: '{file.ContentType}'. Use JPEG, PNG, WebP, GIF ou PDF."
                        });

                    if (file.Length > MaxFileSizeBytes)
                        return Results.BadRequest(new { error = "Arquivo excede o limite de 10 MB." });

                    // Validate actual file bytes — content-type is client-controlled and trivially spoofed
                    if (!await HasValidMagicBytesAsync(file))
                        return Results.BadRequest(new { error = "Conteúdo do arquivo não corresponde ao tipo declarado." });

                    refs.Add(new FileReference(file.OpenReadStream(), file.ContentType, file.FileName));
                    j++;
                }

                items.Add(new OrderItemCommand(dto.ProductId, dto.Quantity, dto.PaidPrice, dto.Observation, refs));
            }

            try
            {
                var result = await handler.HandleAsync(
                    new CreateOrderCommand(clientName, clientMobile, email, taxId, deliveryDate, items));

                return Results.Ok(new
                {
                    sessionId   = result.SessionId,
                    checkoutUrl = result.CheckoutUrl
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
            catch (PaymentGatewayException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        })
        .DisableAntiforgery()
        .RequireRateLimiting("orders")
        .WithRequestTimeout("orders");

        return app;
    }

    private static bool IsValidEmail(string email)
    {
        try { _ = new MailAddress(email); return true; }
        catch { return false; }
    }

    // Brazilian landline (10 digits) or mobile (11 digits), stripping country code +55 if present
    private static bool IsValidBrazilianPhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("55") && digits.Length > 11) digits = digits[2..];
        return digits.Length is 10 or 11 && digits[0] != '0';
    }

    // Verify the actual file bytes match the declared MIME type — prevents content-type spoofing
    private static async Task<bool> HasValidMagicBytesAsync(IFormFile file)
    {
        var buffer = new byte[12];
        using var stream = file.OpenReadStream();
        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
        if (bytesRead < 4) return false;

        return file.ContentType switch
        {
            "image/jpeg" =>
                buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF,
            "image/png" =>
                buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47,
            "image/webp" =>
                buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46
                && bytesRead >= 12
                && buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50,
            "image/gif" =>
                buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x38,
            "application/pdf" =>
                buffer[0] == 0x25 && buffer[1] == 0x50 && buffer[2] == 0x44 && buffer[3] == 0x46,
            _ => false
        };
    }

    // Full Brazilian CPF checksum validation
    private static bool IsValidCpf(string cpf)
    {
        var digits = cpf.Where(char.IsDigit).ToArray();
        if (digits.Length != 11) return false;

        // Reject all-same-digit CPFs (000.000.000-00 etc.)
        if (digits.Distinct().Count() == 1) return false;

        // First check digit
        var sum = 0;
        for (int i = 0; i < 9; i++) sum += (digits[i] - '0') * (10 - i);
        var remainder = sum % 11;
        var expectedFirst = remainder < 2 ? 0 : 11 - remainder;
        if ((digits[9] - '0') != expectedFirst) return false;

        // Second check digit
        sum = 0;
        for (int i = 0; i < 10; i++) sum += (digits[i] - '0') * (11 - i);
        remainder = sum % 11;
        var expectedSecond = remainder < 2 ? 0 : 11 - remainder;
        return (digits[10] - '0') == expectedSecond;
    }

    private record OrderItemDto(Guid ProductId, int Quantity, int PaidPrice, string? Observation);
}
