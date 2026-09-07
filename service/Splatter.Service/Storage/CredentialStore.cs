// Credential Store - Secure credential management
// Uses OS keychain where available, falls back to encrypted file

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Splatter.Service.Storage;

public sealed class CredentialStore : ICredentialStore
{
    private readonly ILogger<CredentialStore> _logger;
    private readonly ServiceConfiguration _config;
    private readonly string _credentialsPath;
    private readonly byte[] _machineKey;

    public CredentialStore(ILogger<CredentialStore> logger, ServiceConfiguration config)
    {
        _logger = logger;
        _config = config;
        _credentialsPath = Path.Combine(config.DataDirectory, ".credentials");
        _machineKey = GetMachineKey();
    }

    public async Task<string?> GetAsync(string providerId, CancellationToken ct)
    {
        var path = GetCredentialPath(providerId);
        if (!File.Exists(path))
            return null;

        try
        {
            var encrypted = await File.ReadAllBytesAsync(path, ct);
            return Decrypt(encrypted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read credential for {ProviderId}", providerId);
            return null;
        }
    }

    public async Task SetAsync(string providerId, string credential, CancellationToken ct)
    {
        Directory.CreateDirectory(_credentialsPath);
        var path = GetCredentialPath(providerId);

        try
        {
            var encrypted = Encrypt(credential);
            await File.WriteAllBytesAsync(path, encrypted, ct);
            _logger.LogDebug("Stored credential for {ProviderId}", providerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store credential for {ProviderId}", providerId);
            throw;
        }
    }

    public Task DeleteAsync(string providerId, CancellationToken ct)
    {
        var path = GetCredentialPath(providerId);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogDebug("Deleted credential for {ProviderId}", providerId);
        }
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string providerId, CancellationToken ct)
    {
        var path = GetCredentialPath(providerId);
        return Task.FromResult(File.Exists(path));
    }

    private string GetCredentialPath(string providerId)
    {
        // Sanitize provider ID for filename
        var safe = Convert.ToBase64String(Encoding.UTF8.GetBytes(providerId))
            .Replace('/', '_').Replace('+', '-');
        return Path.Combine(_credentialsPath, safe);
    }

    private byte[] Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _machineKey;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        // Prepend IV to ciphertext
        var result = new byte[aes.IV.Length + ciphertext.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(ciphertext, 0, result, aes.IV.Length, ciphertext.Length);

        return result;
    }

    private string Decrypt(byte[] encrypted)
    {
        using var aes = Aes.Create();
        aes.Key = _machineKey;

        // Extract IV from beginning
        var iv = new byte[aes.BlockSize / 8];
        var ciphertext = new byte[encrypted.Length - iv.Length];
        Buffer.BlockCopy(encrypted, 0, iv, 0, iv.Length);
        Buffer.BlockCopy(encrypted, iv.Length, ciphertext, 0, ciphertext.Length);

        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plaintextBytes = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    private byte[] GetMachineKey()
    {
        // Generate a machine-specific key based on machine ID
        // This is a fallback - in production, use OS keychain APIs
        var machineId = Environment.MachineName + Environment.UserName;
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(machineId));
    }
}
