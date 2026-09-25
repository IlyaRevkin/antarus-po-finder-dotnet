using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    public const string FlatKindEmailRule = "email_rule";

    /// <summary>Разделитель внутри строки правила. Вертикальная черта: в почтовом адресе её быть не
    /// может, а в названии категории — тем более (категории свои, из перечисления).</summary>
    private const char EmailRuleSeparator = '|';

    /// <summary>Правила «что кому дублировать на почту».
    ///
    /// Хранятся плоским списком строк «категория|адрес» ровно затем, чтобы ездить между машинами
    /// тем же механизмом, что теги и слова-исключения, — вместе с надгробиями. Настройка общая для
    /// конторы: её заводит администратор один раз, а получают все.</summary>
    public List<EmailRule> GetEmailRules()
    {
        var result = new List<EmailRule>();
        using var reader = ExecuteReader("SELECT name FROM email_rules ORDER BY sort_order, name");
        while (reader.Read())
        {
            var raw = reader.GetString(0);
            var cut = raw.IndexOf(EmailRuleSeparator);
            // Строка без разделителя — правило «на всё» для этого адреса. Так может выглядеть
            // значение, вписанное в базу руками; отбрасывать его значило бы молча не слать письма.
            result.Add(cut < 0
                ? new EmailRule { Id = raw, Category = "", Recipient = raw.Trim() }
                : new EmailRule { Id = raw, Category = raw[..cut].Trim(), Recipient = raw[(cut + 1)..].Trim() });
        }
        return result;
    }

    public void AddEmailRule(string category, string recipient)
    {
        recipient = (recipient ?? "").Trim();
        if (recipient.Length == 0) return;
        AddEmailRuleRaw((category ?? "").Trim() + EmailRuleSeparator + recipient);
    }

    /// <summary>Заводит правило по уже склеенной строке — вход для приёма конфига, которому строки
    /// приезжают ровно в том виде, в каком лежат.</summary>
    public void AddEmailRuleRaw(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;

        // Регистр сворачивается в .NET, а не в SQL: COLLATE NOCASE не трогает кириллицу, а в
        // названии группы AD она бывает всегда.
        var existing = GetEmailRulesRaw().FirstOrDefault(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            MarkFlatListAlive(FlatKindEmailRule, existing);
            return;
        }

        var order = Convert.ToInt32(ExecuteScalar("SELECT COALESCE(MAX(sort_order), 0) + 1 FROM email_rules") ?? 1);
        ExecuteNonQuery("INSERT OR IGNORE INTO email_rules(name, sort_order) VALUES(@n, @s)", cmd =>
        {
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@s", order);
        });
        MarkFlatListAlive(FlatKindEmailRule, name);
    }

    public List<string> GetEmailRulesRaw()
    {
        var result = new List<string>();
        using var reader = ExecuteReader("SELECT name FROM email_rules ORDER BY sort_order, name");
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public void DeleteEmailRule(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;
        ExecuteNonQuery("DELETE FROM email_rules WHERE name = @n", cmd => cmd.Parameters.AddWithValue("@n", name));
        MarkFlatListDeleted(FlatKindEmailRule, name);
    }
}
