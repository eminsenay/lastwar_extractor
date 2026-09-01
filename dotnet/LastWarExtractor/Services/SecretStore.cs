namespace LastWarExtractor.Services;

/// <summary>Abstraction over encrypted secret storage (SecureStorage on Windows uses DPAPI).</summary>
public interface ISecretStore
{
    Task<string?> GetApiKeyAsync();
    Task SetApiKeyAsync(string value);
    void RemoveApiKey();
}

public sealed class SecureStorageSecretStore : ISecretStore
{
    private const string ApiKeyName = "openai_api_key";

    public async Task<string?> GetApiKeyAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(ApiKeyName).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task SetApiKeyAsync(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            RemoveApiKey();
            return;
        }
        await SecureStorage.Default.SetAsync(ApiKeyName, value).ConfigureAwait(false);
    }

    public void RemoveApiKey() => SecureStorage.Default.Remove(ApiKeyName);
}
