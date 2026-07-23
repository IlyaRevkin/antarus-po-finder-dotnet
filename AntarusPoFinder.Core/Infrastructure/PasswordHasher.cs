using System;
using System.Security.Cryptography;
using System.Text;

namespace AntarusPoFinder.Core.Infrastructure;

/// <summary>Хеширование паролей администратора/программиста (см. ConfigService.SetAdminPassword/
/// SetProgrammerPassword/VerifyAdminPassword/VerifyProgrammerPassword) — раньше пароли лежали в
/// таблице settings открытым текстом, любой, у кого есть доступ к po_finder_config.json или к
/// самому файлу БД, мог прочитать их напрямую. PBKDF2 (Rfc2898DeriveBytes) со случайной солью на
/// каждый пароль и заведомо избыточным числом итераций — стандартный выбор для локального,
/// оффлайн хранимого пароля без внешней инфраструктуры (bcrypt/argon2 потребовали бы стороннего
/// пакета, которого в проекте нет).
///
/// Формат хранимой строки: "pbkdf2$&lt;итерации&gt;$&lt;соль в Base64&gt;$&lt;хеш в Base64&gt;" — число
/// итераций хранится в самой строке (а не берётся из константы ниже), чтобы будущее повышение
/// Iterations не сделало недействительными уже сохранённые хеши — Verify всегда использует то
/// число итераций, с которым конкретный хеш был посчитан.</summary>
public static class PasswordHasher
{
    private const string Prefix = "pbkdf2";
    private const int Iterations = 100_000;
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    /// <summary>Хеширует пароль (в т.ч. пустую строку — пустой пароль программиста, означающий
    /// «пароль не задан», по-прежнему хранится и сравнивается отдельно в ConfigService, сюда не
    /// подставляется — см. SetProgrammerPassword).</summary>
    public static string Hash(string password)
    {
        password ??= "";
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashSizeBytes);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Сравнение с постоянным временем выполнения (CryptographicOperations.FixedTimeEquals)
    /// — обычное строковое/массивное сравнение по байтам прерывается на первом несовпадении, что
    /// теоретически позволяет по времени ответа подбирать хеш побайтово (timing attack); здесь это
    /// не критично (сравнение идёт локально, не по сети), но раз меняем схему хранения — делаем
    /// сразу правильно.</summary>
    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        password ??= "";

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false; // повреждённая/чужая строка в settings — не наш формат, не совпадёт
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Отличает уже хешированное значение (наш формат "pbkdf2$...") от старого plaintext-
    /// пароля — используется миграцией существующих баз (см. Database.MigratePlaintextPasswordsToHashesOnce)
    /// и тестами, не пытается ничего провалидировать глубже самого префикса.</summary>
    public static bool IsHashed(string value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix + "$", StringComparison.Ordinal);
}
