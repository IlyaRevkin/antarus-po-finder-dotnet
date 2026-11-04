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

    // ── Шифрование ПАРОЛЕМ ЧЕЛОВЕКА (формат "APOFPWD1") ──────────────────────────────────────
    //
    // Всё выше шифруется ключом, который лежит внутри exe: от текстового редактора защищает, от
    // того, у кого есть программа, — нет. Для общего конфига на внутренней шаре этого хватает.
    //
    // Резервной копии, уезжающей в S3, не хватает категорически: бакет ПУБЛИЧЕН НА ЧТЕНИЕ (на этом
    // держится QR с наклейки), то есть объект там читает кто угодно из интернета. Поэтому у копии
    // ключ не из программы, а из пароля, который задаёт человек, и в репозитории/в бинарнике его
    // нет вовсе.
    //
    // Схема ТА ЖЕ, что у V2 выше — AES-256-GCM, случайный nonce на каждое шифрование, тег
    // аутентифицирует всё целиком; второго способа шифровать в этом проекте заводить нельзя.
    // Отличий ровно два:
    //   • ключ выводится PBKDF2-HMAC-SHA256 из пароля и СЛУЧАЙНОЙ соли (вместо SHA256 от
    //     захардкоженной фразы) — соль хранится в самом файле, иначе одинаковые пароли на разных
    //     машинах дали бы одинаковые ключи, а радужная таблица по словарю паролей стала бы
    //     осмысленной;
    //   • работаем с байтами, а не со строкой: внутри zip, а не JSON.
    //
    // Формат: [magic "APOFPWD1"][salt 16][iterations int32 LE][nonce 12][tag 16][шифротекст].
    // Число итераций лежит В ФАЙЛЕ, а не берётся из константы при чтении: когда через несколько лет
    // его придётся поднять, старые копии обязаны остаться читаемыми — иначе смысл резервной копии
    // теряется ровно в тот момент, когда она понадобилась.

    private static readonly byte[] MagicHeaderPwd = Encoding.ASCII.GetBytes("APOFPWD1");

    private const int SaltSizeBytes = 16;

    /// <summary>Итераций PBKDF2 для НОВЫХ копий. 210 000 — ориентир OWASP для PBKDF2-HMAC-SHA256;
    /// считается около четверти секунды и делается раз в сутки в фоне, то есть заметить это
    /// неоткуда, а перебор пароля дорожает на пять порядков.</summary>
    public const int PasswordIterations = 210_000;

    private static byte[] DeriveKeyFromPassword(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Зашифровать произвольные байты паролем человека. Пустой пароль — не ошибка вызова, а
    /// запрещённое состояние: копия без пароля в публичном бакете это и есть та утечка, от которой
    /// всё затевалось, поэтому ловим здесь, а не надеемся на проверку у вызывающего.</summary>
    public static byte[] EncryptWithPassword(byte[] plain, string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Пустой пароль: шифровать нечем", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var key = DeriveKeyFromPassword(password, salt, PasswordIterations);
        var cipherBytes = new byte[plain.Length];
        var tag = new byte[TagSizeBytes];

        using (var gcm = new AesGcm(key, TagSizeBytes))
            gcm.Encrypt(nonce, plain, cipherBytes, tag);

        var iterations = BitConverter.GetBytes(PasswordIterations);
        if (!BitConverter.IsLittleEndian) Array.Reverse(iterations);

        var result = new byte[MagicHeaderPwd.Length + SaltSizeBytes + 4 + NonceSizeBytes + TagSizeBytes + cipherBytes.Length];
        var offset = 0;
        Buffer.BlockCopy(MagicHeaderPwd, 0, result, offset, MagicHeaderPwd.Length); offset += MagicHeaderPwd.Length;
        Buffer.BlockCopy(salt, 0, result, offset, salt.Length); offset += salt.Length;
        Buffer.BlockCopy(iterations, 0, result, offset, 4); offset += 4;
        Buffer.BlockCopy(nonce, 0, result, offset, nonce.Length); offset += nonce.Length;
        Buffer.BlockCopy(tag, 0, result, offset, tag.Length); offset += tag.Length;
        Buffer.BlockCopy(cipherBytes, 0, result, offset, cipherBytes.Length);
        return result;
    }

    /// <summary>Расшифровать. null — «не тот пароль, не тот формат или файл испорчен»; различать эти
    /// три случая нечем и не нужно: ответ человеку в любом из них один и тот же — «этим паролем не
    /// открывается».</summary>
    public static byte[]? TryDecryptWithPassword(byte[] data, string password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        if (!StartsWith(data, MagicHeaderPwd)) return null;

        var headerLen = MagicHeaderPwd.Length + SaltSizeBytes + 4 + NonceSizeBytes + TagSizeBytes;
        if (data.Length < headerLen) return null;

        try
        {
            var offset = MagicHeaderPwd.Length;
            var salt = new byte[SaltSizeBytes];
            Buffer.BlockCopy(data, offset, salt, 0, SaltSizeBytes); offset += SaltSizeBytes;

            var iterBytes = new byte[4];
            Buffer.BlockCopy(data, offset, iterBytes, 0, 4); offset += 4;
            if (!BitConverter.IsLittleEndian) Array.Reverse(iterBytes);
            var iterations = BitConverter.ToInt32(iterBytes, 0);
            // Мусор в поле итераций — повод отказать, а не считать час или поделить на ноль.
            if (iterations is < 1000 or > 10_000_000) return null;

            var nonce = new byte[NonceSizeBytes];
            Buffer.BlockCopy(data, offset, nonce, 0, NonceSizeBytes); offset += NonceSizeBytes;
            var tag = new byte[TagSizeBytes];
            Buffer.BlockCopy(data, offset, tag, 0, TagSizeBytes); offset += TagSizeBytes;

            var cipherBytes = new byte[data.Length - headerLen];
            Buffer.BlockCopy(data, offset, cipherBytes, 0, cipherBytes.Length);
            var plain = new byte[cipherBytes.Length];

            using var gcm = new AesGcm(DeriveKeyFromPassword(password, salt, iterations), TagSizeBytes);
            gcm.Decrypt(nonce, cipherBytes, tag, plain); // неверный пароль — несовпадение тега — исключение
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Похоже ли это на копию, зашифрованную паролем. Нужно одному месту — списку копий,
    /// который рисует замочек рядом с теми, что без пароля не открыть.</summary>
    public static bool IsPasswordEncrypted(byte[] head) => StartsWith(head, MagicHeaderPwd);

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
