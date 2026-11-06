using DcsVoiceCom.DcsBios;

namespace DcsVoiceCom.Definitions;

/// <summary>
/// Sends controls with the catalog's exact spelling. DCS-BIOS names are case-sensitive
/// ("ARC210_100kHZ_SEL" does nothing, "ARC210_100KHZ_SEL" turns the wheel), but profiles are matched
/// case-insensitively, so a name typed in the wrong case would otherwise be sent and silently ignored.
/// It also turns INC/DEC into -/+ the suggested step for knobs that only take a variable step
/// (the A-10C ARC-210 channel knob), since DCS-BIOS ignores INC/DEC there.
/// </summary>
public sealed class CatalogBiosSender(IBiosSender inner, Func<ControlCatalog> catalog) : IBiosSender
{
    public Task SendAsync(string controlId, string argument, CancellationToken cancellationToken = default)
    {
        var control = catalog().Find(controlId.Trim());
        return inner.SendAsync(control?.Identifier ?? controlId, control is null ? argument : Argument(control, argument), cancellationToken);
    }

    /// <summary>INC/DEC as -/+ the suggested step when the control has a variable step but no fixed step; otherwise the argument as given.</summary>
    public static string Argument(ControlDefinition control, string argument)
    {
        var up = string.Equals(argument, "INC", StringComparison.OrdinalIgnoreCase);
        if (!up && !string.Equals(argument, "DEC", StringComparison.OrdinalIgnoreCase)) return argument;
        if (control.FindInput(InputInterface.FixedStep) != null) return argument;
        if (control.FindInput(InputInterface.VariableStep) is not { } knob) return argument;
        var step = (knob.SuggestedStep ?? 3200).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return (up ? "+" : "-") + step;
    }

    /// <summary>The catalog's spelling of <paramref name="controlId"/>, or the name as given when the catalog doesn't know it.</summary>
    public static string Canonical(ControlCatalog catalog, string controlId) =>
        catalog.Find(controlId.Trim())?.Identifier ?? controlId;
}
