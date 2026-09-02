namespace AntarusPoFinder.Core.Domain;

/// <summary>Sub-type within a group: КПЧ, ВЗУ, КНС, ПП, ХП, FD. "—" means no sub-type.</summary>
public class EquipmentSubType
{
    public int? Id { get; set; }
    public int GroupId { get; set; }
    public string Name { get; set; } = "";
    public int Prefix { get; set; }
    public string FolderName { get; set; } = "";
    public int SortOrder { get; set; }

    /// <summary>Stable cross-machine identity for config sync — survives renames, unlike Name.</summary>
    public string SyncId { get; set; } = "";

    /// <summary>Last local edit timestamp — see EquipmentGroup.UpdatedAt for format/purpose.</summary>
    public string UpdatedAt { get; set; } = "";

    /// <summary>На этот шкаф инструкции НЕ БУДЕТ вовсе — рациональные шкафы (см. StubKind.NotPlanned).
    ///
    /// Признак стоит именно у ПОДТИПА, а не у версии и не у контроллера: «инструкция не пишется» — это
    /// свойство шкафа как изделия, а подтип в этом справочнике и есть изделие (ПЖ-2.0, НГР-КНС, …).
    /// Поставь его у версии — его пришлось бы вспоминать при каждой загрузке прошивки, и одна забытая
    /// галочка давала бы шкафу обещание «инструкция в разработке», которое никогда не сбудется.</summary>
    public bool NoInstruction { get; set; }
}
