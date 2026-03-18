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
            var response = await client.PostAsJsonAsync("/v1/customer/create", body, _json);
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


    public async Task<AbacateBillingResponse?> CreateBillingAsync(CreateAbacateBillingRequest request)
    {
        var client = httpClientFactory.CreateClient("AbacatePay");
        try
        {
            var response = await client.PostAsJsonAsync("/v1/billing/create", request, _json);
            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("AbacatePay CreateBilling falhou {Status}: {Body}", response.StatusCode, content);
                return null;
            }
            return JsonSerializer.Deserialize<AbacateBillingResponse>(content, _json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AbacatePay CreateBilling exception");
            return null;
        }
    }

}
