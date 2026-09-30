namespace Forge.Kernel.Security;

/// <summary>
/// Handling markings on data. A person may see a resource only if they hold every marking the
/// resource carries. Markings are a set, not a ladder: CUI and export control are independent.
/// </summary>
[Flags]
public enum DataMarking
{
    None = 0,
    ControlledUnclassified = 1,
    ExportControlled = 2,
    Proprietary = 4,
}
