namespace LastWarExtractor.Services;

/// <summary>Abstraction over encrypted secret storage (SecureStorage on Windows uses DPAPI).
/// API keys are stored independently per provider ("openai", "gemini", "local", "custom").</summary>
public interface ISecretStore
{
    Task<string?> GetApiKeyAsync(string provider);
    Task SetApiKeyAsync(string provider, string value);
    void RemoveApiKey(string provider);
}

public sealed class SecureStorageSecretStore : ISecretStore
{
    // Legacy single-key name from before per-provider storage; migrated on first read.
    private const string LegacyApiKeyName = "openai_api_key";

    private static string KeyName(string provider)
    {
        var p = provider.Trim().ToLowerInvariant();
        if (p is not ("openai" or "gemini" or "local" or "custom"))
            p = "custom";
        return $"api_key_{p}";
    }

    public async Task<string?> GetApiKeyAsync(string provider)
    {
        try
        {
            var value = await SecureStorage.Default.GetAsync(KeyName(provider)).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(value))
                return value;

            var legacy = await SecureStorage.Default.GetAsync(LegacyApiKeyName).ConfigureAwait(false);
            if (string.IsNullOrEmpty(legacy))
                return null;

            await SecureStorage.Default.SetAsync(KeyName(provider), legacy).ConfigureAwait(false);
            SecureStorage.Default.Remove(LegacyApiKeyName);
            return legacy;
        }
        catch
        {
            return null;
        }
    }

    public async Task SetApiKeyAsync(string provider, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            RemoveApiKey(provider);
            return;
        }
        await SecureStorage.Default.SetAsync(KeyName(provider), value).ConfigureAwait(false);
    }

    public void RemoveApiKey(string provider) => SecureStorage.Default.Remove(KeyName(provider));
}
