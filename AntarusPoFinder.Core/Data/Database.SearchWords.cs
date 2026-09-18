using System;
using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    public const string FlatKindOnDemandWord = "search_on_demand_word";

    /// <summary>Слова-исключения: прошивка, в описании которой встретилось такое слово, не
    /// показывается в выдаче, пока это слово не появится в самом запросе.
    ///
    /// Просьба Ильи: «когда я ищу шкаф НГР-ПП-2-(2.5-4А)-Рх, я ищу Рх и всё ок, а когда
    /// НГР-ПП-2-(2.5-4А), мне выдаёт Рх тоже, а он мне не нужен». Совпадение там честное — короткий
    /// запрос является префиксом длинного названия, — поэтому решать должен человек.
    ///
    /// Список ОБЩИЙ, а не поле у каждой прошивки: слово «Рх» относится не к одной записи, а ко всем,
    /// где оно встречается, и расставлять пометку руками у каждой новой прошивки означало бы
    /// заводить работу, которую забудут сделать. Завёл слово один раз — и оно действует на всё, что
    /// появится потом.</summary>
    public List<string> GetOnDemandWords()
    {
        var result = new List<string>();
        using var reader = ExecuteReader("SELECT name FROM search_on_demand_words ORDER BY sort_order, name");
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Завести слово (или подтвердить, что оно живое).
    ///
    /// Регистр сворачивается в .NET, а не в SQL: SQLite COLLATE NOCASE не трогает кириллицу, и «Рх»
    /// с «РХ» стали бы двумя строками таблицы — а словарь по именам с OrdinalIgnoreCase, который
    /// строит приём конфига, на таком дубликате падает.</summary>
    public void AddOnDemandWord(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;

        var existing = GetOnDemandWords().FirstOrDefault(w => string.Equals(w, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            MarkFlatListAlive(FlatKindOnDemandWord, existing);
            return;
        }

        var order = Convert.ToInt32(ExecuteScalar("SELECT COALESCE(MAX(sort_order), 0) + 1 FROM search_on_demand_words") ?? 1);
        ExecuteNonQuery("INSERT OR IGNORE INTO search_on_demand_words(name, sort_order) VALUES(@n, @s)", cmd =>
        {
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@s", order);
        });
        MarkFlatListAlive(FlatKindOnDemandWord, name);
    }

    /// <summary>Убрать слово. Прошивки при этом не трогаются вовсе — слово нигде у них не записано,
    /// оно только меняет правило показа; убрали слово, и всё снова находится как раньше.</summary>
    public void DeleteOnDemandWord(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;
        ExecuteNonQuery("DELETE FROM search_on_demand_words WHERE name = @n COLLATE NOCASE",
            cmd => cmd.Parameters.AddWithValue("@n", name));
        MarkFlatListDeleted(FlatKindOnDemandWord, name);
    }
}
