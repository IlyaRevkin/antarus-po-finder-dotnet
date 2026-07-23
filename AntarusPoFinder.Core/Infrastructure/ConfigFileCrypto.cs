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
/// app itself. Every machine running this app derives the exact same key from the same passphrase —
/// required since ANY machine must be able to decrypt what ANY OTHER machine last exported.
///
/// Формат V2 (текущий, AES-GCM): [magic "APOFCFG2"][nonce 12 байт][tag 16 байт][шифротекст]. Nonce —
/// случайный на КАЖДОЕ шифрование (в отличие от V1 ниже), tag аутентифицирует и шифротекст, и то,
/// что файл вообще не был подменён/повреждён на шаре — TryDecrypt возвращает null при любом
/// несовпадении тега, а не молча отдаёт мусор после расшифровки. Ключ по-прежнему выводится из
/// захардкоженной фразы (тот же осознанный компромисс клиентского секрета, что и раньше) — то, что
/// действительно изменилось, это переход от «шифр без аутентификации» к «шифр с аутентификацией»,
/// не секретность ключа как таковая.
///
/// Формат V1 (устаревший, но читаемый — AES-CBC с фиксированным IV, БЕЗ аутентификации): оставлен
/// только для чтения (TryDecrypt), потому что на дисках коллег, которые ещё не обновили приложение,
/// на момент этого фикса реально лежат файлы, зашифрованные именно так — если TryDecrypt перестанет
/// их понимать, эти машины не смогут прочитать существующий общий конфиг, пока не выполнят полный
/// новый экспорт. Encrypt() больше никогда не пишет V1.</summary>
public static class ConfigFileCrypto
{
    private const string Passphrase = "AntarusPoFinder.NetworkConfig.v1.4f2b9c7e";

    private static readonly byte[] MagicHeaderV1 = Encoding.ASCII.GetBytes("APOFCFG1");
    private static readonly byte[] MagicHeaderV2 = Encoding.ASCII.GetBytes("APOFCFG2");

    private const int NonceSizeBytes = 12; // стандартный размер nonce для AES-GCM
    private const int TagSizeBytes = 16;

    private static byte[] DeriveKey() => SHA256.HashData(Encoding.UTF8.GetBytes(Passphrase)); // 32 байта -> AES-256

    /// <summary>Ключ+IV для устаревшего V1-формата — сохранено byte-в-byte как было, только чтобы
    /// TryDecrypt мог прочитать файлы, зашифрованные до этого фикса. Ничего новое этим методом
    /// больше не шифруется.</summary>
    private static (byte[] Key, byte[] Iv) DeriveKeyIvLegacyV1()
    {
        var hash = DeriveKey();
        var iv = new byte[16];
        Array.Copy(hash, iv, 16);
        return (hash, iv);
    }

    /// <summary>Всегда пишет текущий (V2, AES-GCM) формат — см. класс. Сигнатура (string -> byte[])
    /// не изменилась, чтобы не трогать вызывающий код (ConfigSyncService.PrepareExport/WriteExport).</summary>
    public static byte[] Encrypt(string plaintext)
    {
        var key = DeriveKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes); // случайный на каждое шифрование
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSizeBytes];

        using (var gcm = new AesGcm(key, TagSizeBytes))
            gcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var result = new byte[MagicHeaderV2.Length + nonce.Length + tag.Length + cipherBytes.Length];
        var offset = 0;
        Buffer.BlockCopy(MagicHeaderV2, 0, result, offset, MagicHeaderV2.Length); offset += MagicHeaderV2.Length;
        Buffer.BlockCopy(nonce, 0, result, offset, nonce.Length); offset += nonce.Length;
        Buffer.BlockCopy(tag, 0, result, offset, tag.Length); offset += tag.Length;
        Buffer.BlockCopy(cipherBytes, 0, result, offset, cipherBytes.Length);
        return result;
    }

    /// <summary>Returns null if data doesn't match either known encrypted format (or fails to
    /// authenticate/decrypt) — callers treat that as "not our encrypted format" and fall back to
    /// parsing the bytes as legacy plain-text JSON (see ConfigSyncService.ParseBytes), so a shared
    /// config exported by an app version from before encryption existed at all keeps working.
    ///
    /// Порядок проверки: сперва текущий V2 (AES-GCM — magic "APOFCFG2"), затем устаревший V1
    /// (AES-CBC — magic "APOFCFG1"). Оба магических заголовка достаточно различимы (разный
    /// последний байт), так что перепутать форматы между собой невозможно.</summary>
    public static string? TryDecrypt(byte[] data)
    {
        if (StartsWith(data, MagicHeaderV2)) return TryDecryptV2Gcm(data);
        if (StartsWith(data, MagicHeaderV1)) return TryDecryptV1LegacyCbc(data);
        return null;
    }

    private static bool StartsWith(byte[] data, byte[] header)
    {
        if (data.Length < header.Length) return false;
        for (int i = 0; i < header.Length; i++)
            if (data[i] != header[i]) return false;
        return true;
    }

    private static string? TryDecryptV2Gcm(byte[] data)
    {
        var headerLen = MagicHeaderV2.Length + NonceSizeBytes + TagSizeBytes;
        if (data.Length < headerLen) return null; // короче, чем nonce+tag — точно не валидный файл

        try
        {
            var nonce = new byte[NonceSizeBytes];
            Buffer.BlockCopy(data, MagicHeaderV2.Length, nonce, 0, NonceSizeBytes);
            var tag = new byte[TagSizeBytes];
            Buffer.BlockCopy(data, MagicHeaderV2.Length + NonceSizeBytes, tag, 0, TagSizeBytes);
            var cipherBytes = new byte[data.Length - headerLen];
            Buffer.BlockCopy(data, headerLen, cipherBytes, 0, cipherBytes.Length);
            var plainBytes = new byte[cipherBytes.Length];

            using var gcm = new AesGcm(DeriveKey(), TagSizeBytes);
            gcm.Decrypt(nonce, cipherBytes, tag, plainBytes); // бросает, если тег не совпал — подмена/повреждение
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string? TryDecryptV1LegacyCbc(byte[] data)
    {
        try
        {
            var (key, iv) = DeriveKeyIvLegacyV1();
            using var aes = Aes.Create();
            aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            var cipherBytes = new byte[data.Length - MagicHeaderV1.Length];
            Buffer.BlockCopy(data, MagicHeaderV1.Length, cipherBytes, 0, cipherBytes.Length);
            var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
