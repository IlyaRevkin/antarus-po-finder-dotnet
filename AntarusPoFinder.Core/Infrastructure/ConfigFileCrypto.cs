using System;
using System.Security.Cryptography;
using System.Text;

namespace AntarusPoFinder.Core.Infrastructure;

/// <summary>Encrypts the shared network config file (po_finder_config.json) so it doesn't read as
/// plain JSON when opened directly off the network share — a colleague with write access to
/// "Конфиг\" shouldn't be able to casually open it in Notepad and hand-edit settings, tags, fw_version
/// rows etc. behind the app's back. This is deliberately NOT a defense against a determined attacker
/// who has the app's own binary — the key below ships inside it like any client-side secret, so
/// anyone willing to decompile the app can always get it back out. The goal is only to make the file
/// opaque to a text editor / "просто зашёл в JSON поменял", not to resist reverse engineering of the
/// app itself. Every machine running this app derives the exact same key/IV from the same passphrase
/// — required since ANY machine must be able to decrypt what ANY OTHER machine last exported.</summary>
public static class ConfigFileCrypto
{
    private const string Passphrase = "AntarusPoFinder.NetworkConfig.v1.4f2b9c7e";
    private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("APOFCFG1");

    private static (byte[] Key, byte[] Iv) DeriveKeyIv()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Passphrase));
        var iv = new byte[16];
        Array.Copy(hash, iv, 16);
        return (hash, iv); // hash is 32 bytes -> AES-256 key; first 16 reused as IV (see class doc —
                            // a deterministic IV is acceptable here because every ciphertext is a
                            // full, independent file rewrite, never a stream of related messages
                            // encrypted under the same key that an IV reuse could correlate).
    }

    public static byte[] Encrypt(string plaintext)
    {
        var (key, iv) = DeriveKeyIv();
        using var aes = Aes.Create();
        aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var result = new byte[MagicHeader.Length + cipherBytes.Length];
        Buffer.BlockCopy(MagicHeader, 0, result, 0, MagicHeader.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, MagicHeader.Length, cipherBytes.Length);
        return result;
    }

    /// <summary>Returns null if data doesn't start with our magic header (or fails to decrypt as
    /// valid AES/PKCS7) — callers treat that as "not our encrypted format" and fall back to parsing
    /// the bytes as legacy plain-text JSON, so a shared config exported by an app version from before
    /// this feature keeps working until every machine has upgraded.</summary>
    public static string? TryDecrypt(byte[] data)
    {
        if (data.Length < MagicHeader.Length) return null;
        for (int i = 0; i < MagicHeader.Length; i++)
            if (data[i] != MagicHeader[i]) return null;

        try
        {
            var (key, iv) = DeriveKeyIv();
            using var aes = Aes.Create();
            aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            var cipherBytes = new byte[data.Length - MagicHeader.Length];
            Buffer.BlockCopy(data, MagicHeader.Length, cipherBytes, 0, cipherBytes.Length);
            var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
