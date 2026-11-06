using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Сверка ростера «Пользователи» с составом AD-групп — ровно та половина, которую можно
/// проверить без живого домена. Сам поход в AD (AntarusPoFinder.App.AdGroupMembers) сюда не
/// дотягивается принципиально: там нужен контроллер домена, поэтому вся логика решений вынесена в
/// Core, и проверяется здесь.
///
/// Чиним жалобу Ильи (вторую подряд): «не все авторизованные пользователи отображаются во вкладке
/// „Пользователи", и если даже отображается, не у всех верная роль».</summary>
public class AdRosterSyncTests
{
    private static DateTime Now => new(2026, 11, 5, 12, 0, 0);

    [Fact]
    public void ResolveRoles_TakesMostPrivilegedGroup()
    {
        var resolved = AdRosterSync.ResolveRoles(new[]
        {
            new AdGroupMember("naladchik", "revkin.i"),
            new AdGroupMember("administrator", "revkin.i"),
            new AdGroupMember("programmer", "petrov"),
        });

        // Человек в группе админов и в группе наладчиков — администратор, как и при входе.
        Assert.Equal("administrator", resolved["revkin.i"]);
        Assert.Equal("programmer", resolved["petrov"]);
    }

    [Fact]
    public void ResolveRoles_NormalizesLoginsTheSameWayLoginDoes()
    {
        // AD отдаёт sAMAccountName как придётся по регистру, а ростер хранит нормализованный логин.
        // Без общей нормализации сверка завела бы ВТОРУЮ строку тому же человеку — то есть ровно
        // ту кашу в списке, на которую и жалуются.
        var resolved = AdRosterSync.ResolveRoles(new[] { new AdGroupMember("programmer", "ELITA\\Revkin.I") });

        Assert.True(resolved.ContainsKey("revkin.i"));
    }

    [Fact]
    public void Apply_AddsPeopleWhoNeverLoggedIn()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var result = AdRosterSync.Apply(db, new[] { new AdGroupMember("administrator", "novikov") }, complete: true, Now);

        var user = db.FindAppUserByLogin("novikov");
        Assert.NotNull(user);
        Assert.Equal("administrator", user!.Role);
        Assert.Equal(AppUserRoleSource.Domain, user.RoleSource);
        Assert.True(user.DomainFound);
        Assert.Equal(1, result.Added);
        // Входа не было — выдумывать дату входа нельзя, иначе столбцы «первый/последний вход»
        // перестали бы значить то, что написано.
        Assert.Equal("", user.FirstLoginAt);
        Assert.Equal("", user.LastLoginAt);
    }

    [Fact]
    public void Apply_FixesStaleRoleFromFirstLogin()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        // Так выглядит пострадавший: вошёл когда-то давно, получил минимальную роль по умолчанию,
        // а в домене он администратор.
        db.TouchOrCreateAppUser("revkin.i");
        Assert.Equal("naladchik", db.FindAppUserByLogin("revkin.i")!.Role);

        var result = AdRosterSync.Apply(db, new[] { new AdGroupMember("administrator", "revkin.i") }, complete: true, Now);

        var user = db.FindAppUserByLogin("revkin.i")!;
        Assert.Equal("administrator", user.Role);
        Assert.Equal(AppUserRoleSource.Domain, user.RoleSource);
        Assert.Equal(1, result.RoleChanged);
    }

    [Fact]
    public void Apply_DoesNotDeleteAnyone_ButMarksThemMissing()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        db.TouchOrCreateAppUser("uvolen");
        db.SetAppUserRole(db.FindAppUserByLogin("uvolen")!.Id!.Value, "programmer");

        var result = AdRosterSync.Apply(db, new[] { new AdGroupMember("administrator", "revkin.i") }, complete: true, Now);

        var user = db.FindAppUserByLogin("uvolen");
        Assert.NotNull(user);
        // Роль не тронута — удаление остаётся отдельным осознанным действием администратора.
        Assert.Equal("programmer", user!.Role);
        Assert.Equal(AppUserRoleSource.Manual, user.RoleSource);
        Assert.False(user.DomainFound);
        Assert.NotEqual("", user.DomainCheckedAt);
        Assert.Equal(1, result.NotFoundInDomain);
        Assert.Contains("не найден", AppUserRoleSource.Label(user));
    }

    /// <summary>Сверку делают ИЗ ОДНОГО домена, а люди в ростере бывают из разных. Тот, кто входит
    /// из другого домена, в этих группах отсутствует по совершенно естественной причине —
    /// помечать его «в домене не найден» значит соврать, а промолчать значит снова оставить
    /// администратора с «список опять неверный».</summary>
    [Fact]
    public void Apply_PersonFromAnotherDomain_IsNamed_NotMarkedMissing()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var petrov = db.TouchOrCreateAppUser("petrov").Id!.Value;
        db.RecordAppUserLogonDomain(petrov, "antarus.local");
        var sidorov = db.TouchOrCreateAppUser("sidorov").Id!.Value;
        db.RecordAppUserLogonDomain(sidorov, "ELITA");      // тот же домен, короткое имя

        var result = AdRosterSync.Apply(db, new[] { new AdGroupMember("administrator", "revkin.i") },
            complete: true, Now, scanDomain: "elita.local");

        // Петров — из другого домена: назван списком, пометка не поставлена.
        Assert.Equal(1, result.OtherDomain);
        Assert.Contains(result.OtherDomainLogins, x => x.Contains("petrov") && x.Contains("antarus.local"));
        Assert.Equal("", db.FindAppUserByLogin("petrov")!.DomainCheckedAt);

        // Сидоров входит из ТОГО ЖЕ домена (ELITA = elita.local) — про него вывод честный.
        Assert.False(db.FindAppUserByLogin("sidorov")!.DomainFound);
        Assert.Equal(1, result.NotFoundInDomain);
    }

    /// <summary>Домен сверки не указан — утверждать «он из другого домена» не из чего, и поведение
    /// остаётся ровно прежним.</summary>
    [Fact]
    public void Apply_WithoutScanDomain_BehavesExactlyAsBefore()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var petrov = db.TouchOrCreateAppUser("petrov").Id!.Value;
        db.RecordAppUserLogonDomain(petrov, "antarus.local");

        var result = AdRosterSync.Apply(db, new[] { new AdGroupMember("administrator", "revkin.i") }, complete: true, Now);

        Assert.Equal(0, result.OtherDomain);
        Assert.Equal(1, result.NotFoundInDomain);
        Assert.False(db.FindAppUserByLogin("petrov")!.DomainFound);
    }

    [Fact]
    public void Apply_IncompleteScan_NeverClaimsSomebodyIsMissingFromDomain()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        db.TouchOrCreateAppUser("petrov");

        // Прочитана была не вся настроенная часть домена (нет прав на группу, группа переименована,
        // связь оборвалась) — объявлять остальных «в домене нет» по такому ответу нельзя.
        var result = AdRosterSync.Apply(db, new[] { new AdGroupMember("administrator", "revkin.i") }, complete: false, Now);

        var user = db.FindAppUserByLogin("petrov")!;
        Assert.Equal("", user.DomainCheckedAt);
        Assert.Equal(0, result.NotFoundInDomain);
        Assert.Equal("С первого входа", AppUserRoleSource.Label(user));
    }

    [Fact]
    public void Apply_ManualRoleIsOverwrittenByDomain_AndSaysSo()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = db.TouchOrCreateAppUser("petrov").Id!.Value;
        db.SetAppUserRole(id, "administrator");             // администратор выставил руками
        Assert.Equal(AppUserRoleSource.Manual, db.FindAppUserByLogin("petrov")!.RoleSource);

        AdRosterSync.Apply(db, new[] { new AdGroupMember("naladchik", "petrov") }, complete: true, Now);

        var user = db.FindAppUserByLogin("petrov")!;
        // Источник истины — домен, и в таблице теперь прямо написано, откуда эта роль.
        Assert.Equal("naladchik", user.Role);
        Assert.Equal("Из домена", AppUserRoleSource.Label(user));
    }

    [Fact]
    public void Apply_BumpsRoleUpdatedAt_SoTheTruthWinsTheMergeOnOtherMachines()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = db.TouchOrCreateAppUser("petrov").Id!.Value;
        db.SetAppUserRole(id, "naladchik", AppUserRoleSource.FirstLogin);
        var before = db.FindAppUserByLogin("petrov")!.RoleUpdatedAt;

        System.Threading.Thread.Sleep(1100);
        AdRosterSync.Apply(db, new[] { new AdGroupMember("administrator", "petrov") }, complete: true, Now);

        var after = db.FindAppUserByLogin("petrov")!.RoleUpdatedAt;
        Assert.True(string.CompareOrdinal(after, before) > 0,
            "без свежей отметки времени правда из домена проиграла бы слияние на чужой машине");
    }

    [Fact]
    public void MarkAppUserDomainChecked_DoesNotTouchRoleDecisionTimestamp()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = db.TouchOrCreateAppUser("petrov").Id!.Value;
        var before = db.FindAppUserByLogin("petrov")!.RoleUpdatedAt;

        db.MarkAppUserDomainChecked(id, found: false, "2026-11-05 12:00");

        var user = db.FindAppUserByLogin("petrov")!;
        // «Мы на него посмотрели» не должно выглядеть более свежим решением о роли, чем чужая
        // настоящая правка: иначе сверка затирала бы роли, выставленные на другой машине.
        Assert.Equal(before, user.RoleUpdatedAt);
        Assert.Equal("2026-11-05 12:00", user.DomainCheckedAt);
    }

    [Fact]
    public void RoleSource_SurvivesConfigSyncAlongsideTheRole()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        db.MergeAppUsersOnly(new List<ExportedAppUser>
        {
            new()
            {
                SyncId = "s-1", AdLogin = "revkin.i", Role = "administrator",
                RoleUpdatedAt = "2026-11-05T10:00:00", RoleSource = AppUserRoleSource.Domain,
                DomainCheckedAt = "2026-11-05 10:00", DomainFound = true,
            },
        });

        var user = db.FindAppUserByLogin("revkin.i")!;
        Assert.Equal(AppUserRoleSource.Domain, user.RoleSource);
        Assert.True(user.DomainFound);

        // Снимок со старой версии приложения полей не содержит — источник честно станет
        // «неизвестно», а не выдуманным, но только вместе с ролью, которая выиграла по времени.
        db.MergeAppUsersOnly(new List<ExportedAppUser>
        {
            new() { SyncId = "s-1", AdLogin = "revkin.i", Role = "naladchik", RoleUpdatedAt = "2026-11-05T11:00:00" },
        });

        var after = db.FindAppUserByLogin("revkin.i")!;
        Assert.Equal("naladchik", after.Role);
        Assert.Equal(AppUserRoleSource.Unknown, after.RoleSource);
        // А вот сверка с доменом — наблюдение, а не решение: более поздней в приехавшем снимке нет,
        // значит местная остаётся.
        Assert.Equal("2026-11-05 10:00", after.DomainCheckedAt);
    }

    [Fact]
    public void DomainCheck_TravelsBetweenMachines_IndependentlyOfTheRoleDecision()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = db.TouchOrCreateAppUser("petrov").Id!.Value;
        db.SetAppUserRole(id, "programmer");                      // свежее местное решение о роли

        // Приехала БОЛЕЕ СТАРАЯ роль, но БОЛЕЕ СВЕЖАЯ сверка с доменом: сверку делают с одной
        // машины, а видеть её результат должны все.
        db.MergeAppUsersOnly(new List<ExportedAppUser>
        {
            new()
            {
                SyncId = db.FindAppUserByLogin("petrov")!.SyncId, AdLogin = "petrov", Role = "naladchik",
                RoleUpdatedAt = "2000-01-01T00:00:00",
                DomainCheckedAt = "2026-11-05 12:00", DomainFound = false,
            },
        });

        var user = db.FindAppUserByLogin("petrov")!;
        Assert.Equal("programmer", user.Role);
        Assert.Equal("2026-11-05 12:00", user.DomainCheckedAt);
        Assert.False(user.DomainFound);
    }
}
