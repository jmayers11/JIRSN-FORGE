using System.Globalization;

namespace Forge.Planning.Engines;

/// <summary>Quantities as people read them: no trailing zeros from database scale (26, not 26.000000).</summary>
public static class Quantities
{
    public static decimal Normal(decimal value) => value / 1.000000000000000000000000000000000m;

    public static string Text(decimal value) => Normal(value).ToString(CultureInfo.InvariantCulture);
}
