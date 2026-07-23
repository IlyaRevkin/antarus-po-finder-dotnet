using AntarusPoFinder.Core.Infrastructure;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Covers PasswordHasher — the PBKDF2-based replacement for storing admin_password/
/// programmer_password as plaintext in settings (see ConfigService.SetAdminPassword/VerifyAdminPassword
/// and the security-fix round this belongs to).</summary>
public class PasswordHasherTests
{
    [Fact]
    public void Hash_ThenVerify_SamePassword_Succeeds()
    {
        var stored = PasswordHasher.Hash("correct horse battery staple");
        Assert.True(PasswordHasher.Verify("correct horse battery staple", stored));
    }

    [Fact]
    public void Verify_WrongPassword_Fails()
    {
        var stored = PasswordHasher.Hash("12345");
        Assert.False(PasswordHasher.Verify("54321", stored));
    }

    [Fact]
    public void Hash_StoresInExpectedPbkdf2Format()
    {
        var stored = PasswordHasher.Hash("12345");
        var parts = stored.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2", parts[0]);
        Assert.True(int.Parse(parts[1]) >= 100_000); // требование фикса: не меньше 100k итераций
    }

    [Fact]
    public void Hash_NeverContainsThePlaintextPassword()
    {
        var stored = PasswordHasher.Hash("SuperSecret123");
        Assert.DoesNotContain("SuperSecret123", stored);
    }

    [Fact]
    public void Hash_SamePasswordTwice_ProducesDifferentStrings()
    {
        // Случайная соль на каждый вызов — иначе одинаковые пароли у разных ролей/машин давали бы
        // одинаковый хеш, что облегчает атаку по радужным таблицам.
        var a = PasswordHasher.Hash("12345");
        var b = PasswordHasher.Hash("12345");
        Assert.NotEqual(a, b);
        Assert.True(PasswordHasher.Verify("12345", a));
        Assert.True(PasswordHasher.Verify("12345", b));
    }

    [Fact]
    public void Verify_EmptyStoredValue_ReturnsFalse()
    {
        Assert.False(PasswordHasher.Verify("anything", ""));
    }

    [Fact]
    public void Verify_PlaintextLegacyValue_ReturnsFalseInsteadOfThrowing()
    {
        // Регресс-защита: если строка в settings ещё не мигрирована (не начинается с "pbkdf2$"),
        // Verify должен спокойно вернуть false, а не упасть на разборе формата.
        Assert.False(PasswordHasher.Verify("12345", "12345"));
    }

    [Fact]
    public void Verify_CorruptedBase64InStoredValue_ReturnsFalseInsteadOfThrowing()
    {
        Assert.False(PasswordHasher.Verify("12345", "pbkdf2$100000$not-valid-base64!!$also-not-valid!!"));
    }

    [Fact]
    public void Verify_TamperedHashPortion_Fails()
    {
        var stored = PasswordHasher.Hash("12345");
        var parts = stored.Split('$');
        var tampered = $"{parts[0]}${parts[1]}${parts[2]}${new string('A', parts[3].Length)}";
        Assert.False(PasswordHasher.Verify("12345", tampered));
    }

    [Fact]
    public void IsHashed_RecognizesOwnFormatOnly()
    {
        Assert.True(PasswordHasher.IsHashed(PasswordHasher.Hash("12345")));
        Assert.False(PasswordHasher.IsHashed("12345"));
        Assert.False(PasswordHasher.IsHashed(""));
    }

    [Fact]
    public void Hash_EmptyPassword_RoundTrips()
    {
        // Пароль администратора технически может быть сохранён пустым (SetAdminPassword не
        // запрещает это — см. ConfigService doc) — хеш пустой строки должен вести себя как любой
        // другой хеш, не быть особым случаем.
        var stored = PasswordHasher.Hash("");
        Assert.True(PasswordHasher.Verify("", stored));
        Assert.False(PasswordHasher.Verify("12345", stored));
    }
}
