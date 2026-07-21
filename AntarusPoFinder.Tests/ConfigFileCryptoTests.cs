using System.Text;
using AntarusPoFinder.Core.Infrastructure;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Covers ConfigFileCrypto — the shared network config is now written encrypted so it
/// doesn't read as plain JSON in a text editor. Deliberately not a security boundary against a
/// determined attacker with the app binary (see the class's own doc) — these tests only cover the
/// two things that actually matter for the feature to work: round-trip fidelity, and graceful
/// fallback to legacy plain-text JSON so pre-encryption exports still open.</summary>
public class ConfigFileCryptoTests
{
    [Fact]
    public void Encrypt_ThenDecrypt_RoundTripsExactText()
    {
        const string json = "{\"root_path\":\"Z:\\\\Software\",\"exported_by\":\"revkin.i\"}";

        var encrypted = ConfigFileCrypto.Encrypt(json);
        var decrypted = ConfigFileCrypto.TryDecrypt(encrypted);

        Assert.Equal(json, decrypted);
    }

    [Fact]
    public void Encrypt_DoesNotContainPlaintextSubstrings()
    {
        const string json = "{\"admin_password\":\"SuperSecret123\"}";

        var encrypted = ConfigFileCrypto.Encrypt(json);
        var asText = Encoding.Latin1.GetString(encrypted);

        Assert.DoesNotContain("SuperSecret123", asText);
        Assert.DoesNotContain("admin_password", asText);
    }

    [Fact]
    public void TryDecrypt_LegacyPlainTextBytes_ReturnsNull()
    {
        var legacyPlainJson = Encoding.UTF8.GetBytes("{\"root_path\":\"Z:\\\\Software\"}");

        var result = ConfigFileCrypto.TryDecrypt(legacyPlainJson);

        Assert.Null(result);
    }

    [Fact]
    public void TryDecrypt_EmptyOrTooShort_ReturnsNullInsteadOfThrowing()
    {
        Assert.Null(ConfigFileCrypto.TryDecrypt(System.Array.Empty<byte>()));
        Assert.Null(ConfigFileCrypto.TryDecrypt(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void TryDecrypt_CorruptedCiphertext_ReturnsNullInsteadOfThrowing()
    {
        var encrypted = ConfigFileCrypto.Encrypt("{\"a\":1}");
        encrypted[encrypted.Length - 1] ^= 0xFF; // flip last byte -> breaks PKCS7 padding

        var result = ConfigFileCrypto.TryDecrypt(encrypted);

        Assert.Null(result);
    }
}
