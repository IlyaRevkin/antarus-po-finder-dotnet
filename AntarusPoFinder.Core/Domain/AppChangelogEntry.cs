using System;

namespace AntarusPoFinder.Core.Domain;

/// <summary>Одна строка постоянного журнала «что менялось по версиям приложения» — то, что окно
/// «Что нового» показывает разово при обновлении (см. MainWindowViewModel.CheckWhatsNewAsync), но
/// сохранённое, чтобы к нему можно было вернуться и посмотреть историю изменений уже после того, как
/// разовое окно закрыли. Хранится в настройках (ConfigService.AppChangelogHistory) как JSON-список,
/// per-machine (журнал обновлений именно ЭТОЙ установки), поэтому SeenAt — момент, когда обновление
/// увидели здесь, а не дата релиза как таковая.</summary>
public record AppChangelogEntry(string Version, string Notes, DateTime SeenAt);
