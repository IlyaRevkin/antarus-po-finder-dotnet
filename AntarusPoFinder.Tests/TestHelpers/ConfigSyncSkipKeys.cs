using System.Collections.Generic;
using System.Reflection;
using AntarusPoFinder.App.Services;
using Xunit;

namespace AntarusPoFinder.Tests.TestHelpers;

/// <summary>Список ключей настроек, которые НИКОГДА не уезжают в общий конфиг (per-machine) —
/// ConfigSyncService.SkipSettingsKeys, поле закрытое. Читается отражением, а не переписывается
/// копией в каждом тесте: копия разошлась бы с оригиналом ровно тогда, когда это важнее всего.
/// Тот же приём уже применяли ConfigExportSkipSettingsKeysTests и GeneralPassportTests — здесь он
/// вынесен в одно место, чтобы третий и четвёртый потребитель не заводили третью и четвёртую копию
/// этих же шести строк.</summary>
internal static class ConfigSyncSkipKeys
{
    public static HashSet<string> Read()
    {
        var field = typeof(ConfigSyncService).GetField("SkipSettingsKeys", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field); // поле переименовали/удалили — тест должен упасть явно, а не стать пустышкой
        return (HashSet<string>)field!.GetValue(null)!;
    }
}
