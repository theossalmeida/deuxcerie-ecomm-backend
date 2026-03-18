using System.Net.Http.Json;
using System.Text.Json;
using EcommerceApi.Infrastructure.DTOs;

namespace EcommerceApi.Infrastructure.Services;

public class AbacatePayService(
    IHttpClientFactory httpClientFactory,
    ILogger<AbacatePayService> logger)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<AbacateProductResponse?> CreateProductAsync(
        Guid externalId, string name, int priceCents, string? description)
    {
        var client = httpClientFactory.CreateClient("AbacatePay");
        try
        {
            var body = new CreateAbacateProductRequest(
                ExternalId: externalId.ToString(),
                Name: name,
                Price: priceCents,
                Currency: "BRL",
                Description: description,
                ImageUrl: null
            );
            var response = await client.PostAsJsonAsync("/v2/products/create", body, _json);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("AbacatePay CreateProduct falhou {Status}: {Body}", response.StatusCode, content);
                return null;
            }
            return JsonSerializer.Deserialize<AbacateProductResponse>(content, _json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AbacatePay CreateProduct exception");
            return null;
        }
    }

    public async Task DeleteProductAsync(string abacateProductId)
    {
        var client = httpClientFactory.CreateClient("AbacatePay");
        try
        {
            var response = await client.DeleteAsync($"/v2/products/delete?id={abacateProductId}");
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                logger.LogWarning("AbacatePay DeleteProduct falhou {Status}: {Body}", response.StatusCode, content);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AbacatePay DeleteProduct exception para {Id}", abacateProductId);
        }
    }

    public async Task<PixTransparentResponse?> CreatePixTransparentAsync(long amountCents)
    {
        var client = httpClientFactory.CreateClient("AbacatePay");
        try
        {
            var body = new CreatePixTransparentRequest("PIX", new PixTransparentAmountData(amountCents));
            var response = await client.PostAsJsonAsync("/v2/transparents/create", body, _json);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("AbacatePay CreatePixTransparent falhou {Status}: {Body}", response.StatusCode, content);
                return null;
            }
            return JsonSerializer.Deserialize<PixTransparentResponse>(content, _json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AbacatePay CreatePixTransparent exception");
            return null;
        }
    }

    public async Task<PaymentLinkResponse?> CreatePaymentLinkAsync(CreatePaymentLinkRequest request)
    {
        var client = httpClientFactory.CreateClient("AbacatePay");
        try
        {
            var response = await client.PostAsJsonAsync("/v2/payment-links/create", request, _json);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("AbacatePay CreatePaymentLink falhou {Status}: {Body}", response.StatusCode, content);
                return null;
            }
            return JsonSerializer.Deserialize<PaymentLinkResponse>(content, _json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AbacatePay CreatePaymentLink exception");
            return null;
        }
    }
}
