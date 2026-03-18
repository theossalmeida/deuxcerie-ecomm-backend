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

    public async Task<AbacateCustomerResponse?> CreateCustomerAsync(
        string name, string cellphone, string email, string taxId)
    {
        var client = httpClientFactory.CreateClient("AbacatePay");
        try
        {
            var body = new CreateAbacateCustomerRequest(name, cellphone, email, taxId);
            var response = await client.PostAsJsonAsync("/v2/customers/create", body, _json);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("AbacatePay CreateCustomer falhou {Status}: {Body}",
                    response.StatusCode, content);
                return null;
            }
            return JsonSerializer.Deserialize<AbacateCustomerResponse>(content, _json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AbacatePay CreateCustomer exception");
            return null;
        }
    }

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
                logger.LogError("AbacatePay CreateProduct falhou {Status}: {Body}",
                    response.StatusCode, content);
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

    public async Task<AbacateCheckoutResponse?> CreateCheckoutAsync(CreateAbacateCheckoutRequest request)
    {
        var client = httpClientFactory.CreateClient("AbacatePay");
        try
        {
            var response = await client.PostAsJsonAsync("/v2/checkouts/create", request, _json);
            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("AbacatePay CreateCheckout falhou {Status}: {Body}", response.StatusCode, content);
                return null;
            }
            return JsonSerializer.Deserialize<AbacateCheckoutResponse>(content, _json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AbacatePay CreateCheckout exception");
            return null;
        }
    }
}
