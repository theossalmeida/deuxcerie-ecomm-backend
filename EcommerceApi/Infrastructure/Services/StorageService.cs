using System.Security.Cryptography;
using System.Text;

namespace EcommerceApi.Infrastructure.Services;

public class StorageService : IStorageService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _accessKeyId;
    private readonly string _secretAccessKey;
    private readonly string _bucketName;
    private readonly string _endpoint;
    private readonly string _publicBaseUrl;
    private const string Region = "auto";
    private const string Service = "s3";

    public StorageService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _accessKeyId = configuration["R2:AccessKeyId"]
            ?? throw new InvalidOperationException("R2:AccessKeyId is not configured.");
        _secretAccessKey = configuration["R2:SecretAccessKey"]
            ?? throw new InvalidOperationException("R2:SecretAccessKey is not configured.");
        _bucketName = configuration["R2:BucketName"]
            ?? throw new InvalidOperationException("R2:BucketName is not configured.");
        var accountId = configuration["R2:AccountId"]
            ?? throw new InvalidOperationException("R2:AccountId is not configured.");
        _endpoint = $"https://{accountId}.r2.cloudflarestorage.com";
        _publicBaseUrl = (configuration["R2:PublicBaseUrl"]
            ?? throw new InvalidOperationException("R2:PublicBaseUrl is not configured.")).TrimEnd('/');
    }

    public string GetPublicUrl(string objectKey)
        => $"{_publicBaseUrl}/{objectKey}";

    public async Task<string> UploadFileAsync(Stream stream, string objectKey, string contentType)
    {
        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes);

        var now = DateTime.UtcNow;
        var dateStamp = now.ToString("yyyyMMdd");
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");
        var credentialScope = $"{dateStamp}/{Region}/{Service}/aws4_request";
        var host = new Uri(_endpoint).Host;
        var canonicalUri = "/" + string.Join("/", objectKey.Split('/').Select(Uri.EscapeDataString));
        var payloadHash = Sha256Hex(bytes);
        var canonicalHeaders = $"content-type:{contentType}\nhost:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";
        var signedHeaders = "content-type;host;x-amz-content-sha256;x-amz-date";

        var canonicalRequest = string.Join("\n",
            "PUT",
            $"/{_bucketName}{canonicalUri}",
            "",
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        var stringToSign = string.Join("\n",
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            Sha256Hex(canonicalRequest));

        var signature = HmacHex(DeriveSigningKey(dateStamp), stringToSign);
        var authorization = $"AWS4-HMAC-SHA256 Credential={_accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

        var client = _httpClientFactory.CreateClient("R2");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"{_endpoint}/{_bucketName}/{objectKey}");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Falha ao fazer upload da imagem para o R2: {error}");
        }

        return objectKey;
    }

    private byte[] DeriveSigningKey(string dateStamp)
    {
        var kDate = Hmac(Encoding.UTF8.GetBytes($"AWS4{_secretAccessKey}"), dateStamp);
        var kRegion = Hmac(kDate, Region);
        var kService = Hmac(kRegion, Service);
        return Hmac(kService, "aws4_request");
    }

    private static byte[] Hmac(byte[] key, string data)
        => new HMACSHA256(key).ComputeHash(Encoding.UTF8.GetBytes(data));

    private static string HmacHex(byte[] key, string data)
        => Convert.ToHexString(Hmac(key, data)).ToLower();

    private static string Sha256Hex(string data)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLower();

    private static string Sha256Hex(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLower();
}
