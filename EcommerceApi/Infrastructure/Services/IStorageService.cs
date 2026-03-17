namespace EcommerceApi.Infrastructure.Services;

public interface IStorageService
{
    Task<string> UploadFileAsync(Stream stream, string objectKey, string contentType);
    string GetPublicUrl(string objectKey);
}
