using System;
using System.Security.Cryptography;
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
        encrypted[encrypted.Length - 1] ^= 0xFF; // flip last byte -> breaks the GCM auth tag

        var result = ConfigFileCrypto.TryDecrypt(encrypted);

        Assert.Null(result);
    }

    // ── AES-GCM (V2) — переход с CBC без аутентификации, см. класс doc-комментарий ──────────────

    [Fact]
    public void Encrypt_UsesGcmMagicHeader()
    {
        var encrypted = ConfigFileCrypto.Encrypt("{\"a\":1}");
        var header = Encoding.ASCII.GetString(encrypted, 0, 8);
        Assert.Equal("APOFCFG2", header);
    }

    [Fact]
    public void Encrypt_SamePlaintextTwice_ProducesDifferentBytes()
    {
        // Случайный nonce на каждое шифрование — иначе повторный экспорт того же содержимого дал
        // бы предсказуемо совпадающий шифротекст (см. класс doc: раньше IV был фиксированным).
        const string json = "{\"root_path\":\"Z:\\\\Software\"}";
        var first = ConfigFileCrypto.Encrypt(json);
        var second = ConfigFileCrypto.Encrypt(json);

        Assert.NotEqual(first, second);
        Assert.Equal(json, ConfigFileCrypto.TryDecrypt(first));
        Assert.Equal(json, ConfigFileCrypto.TryDecrypt(second));
    }

    [Fact]
    public void TryDecrypt_TamperedAuthTag_ReturnsNull()
    {
        // GCM-специфичный регресс-тест: подделка байта именно внутри тега аутентификации (не
        // шифротекста) должна так же валиться на проверке тега, а не давать похожий на успех
        // результат.
        var encrypted = ConfigFileCrypto.Encrypt("{\"admin_password\":\"pbkdf2$100000$aa$bb\"}");
        const int tagStart = 8 + 12; // magic (8) + nonce (12)
        encrypted[tagStart] ^= 0xFF;

        Assert.Null(ConfigFileCrypto.TryDecrypt(encrypted));
    }

    [Fact]
    public void TryDecrypt_TruncatedGcmData_ReturnsNullInsteadOfThrowing()
    {
        var magicAndPartialNonce = new byte[] { 65, 80, 79, 70, 67, 70, 71, 50, 1, 2, 3 }; // "APOFCFG2" + 3 bytes, short of nonce+tag
        Assert.Null(ConfigFileCrypto.TryDecrypt(magicAndPartialNonce));
    }

    // ── Обратная совместимость: чтение файлов, зашифрованных ДО перехода на GCM ──────────────────

    [Fact]
    public void TryDecrypt_LegacyV1CbcFormat_StillDecrypts()
    {
        // Воспроизводит ровно тот алгоритм, которым ConfigFileCrypto шифровал файлы ДО этого фикса
        // (AES-CBC, фиксированный IV = первые 16 байт SHA256 от той же парольной фразы, magic
        // "APOFCFG1") — независимо от текущей реализации класса, чтобы тест реально проверял
        // совместимость с файлами, уже лежащими на дисках коллег, а не с тем, что сам класс сейчас
        // считает "старым форматом".
        const string passphrase = "AntarusPoFinder.NetworkConfig.v1.4f2b9c7e";
        const string json = "{\"root_path\":\"Z:\\\\Software\",\"exported_by\":\"legacy.machine\"}";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(passphrase));
        var iv = new byte[16];
        Array.Copy(hash, iv, 16);

        byte[] cipherBytes;
        using (var aes = Aes.Create())
        {
            aes.Key = hash; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(json);
            cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        }

        var magic = Encoding.ASCII.GetBytes("APOFCFG1");
        var legacyFile = new byte[magic.Length + cipherBytes.Length];
        Buffer.BlockCopy(magic, 0, legacyFile, 0, magic.Length);
        Buffer.BlockCopy(cipherBytes, 0, legacyFile, magic.Length, cipherBytes.Length);

        var decrypted = ConfigFileCrypto.TryDecrypt(legacyFile);

        Assert.Equal(json, decrypted);
    }

    [Fact]
    public void TryDecrypt_LegacyV1WithCorruptedPadding_ReturnsNullInsteadOfThrowing()
    {
        const string passphrase = "AntarusPoFinder.NetworkConfig.v1.4f2b9c7e";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(passphrase));
        var iv = new byte[16];
        Array.Copy(hash, iv, 16);

        byte[] cipherBytes;
        using (var aes = Aes.Create())
        {
            aes.Key = hash; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes("{\"a\":1}");
            cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        }
        cipherBytes[^1] ^= 0xFF; // breaks PKCS7 padding on decrypt

        var magic = Encoding.ASCII.GetBytes("APOFCFG1");
        var legacyFile = new byte[magic.Length + cipherBytes.Length];
        Buffer.BlockCopy(magic, 0, legacyFile, 0, magic.Length);
        Buffer.BlockCopy(cipherBytes, 0, legacyFile, magic.Length, cipherBytes.Length);

        Assert.Null(ConfigFileCrypto.TryDecrypt(legacyFile));
    }
}
