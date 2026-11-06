using System.Globalization;
using DcsVoiceCom.DcsBios;
using DcsVoiceCom.Definitions;

namespace DcsVoiceCom.Editing;

/// <summary>A control's current cockpit value as text, read straight from the export buffer.</summary>
public static class LiveValue
{
    /// <summary>"1 DN" for a switch with named positions, other integers as numbers, strings as they are; "" without an output.</summary>
    public static string Read(StateBuffer buffer, ControlDefinition control)
    {
        if (control.PrimaryIntegerOutput is { } integer)
        {
            var value = buffer.ReadInteger(integer.Address, integer.Mask ?? 0xFFFF, integer.ShiftBy ?? 0);
            var text = value.ToString(CultureInfo.InvariantCulture);
            return ControlArguments.PositionLabel(control, value) is { } label ? $"{text} {label}" : text;
        }
        if (control.PrimaryStringOutput is { } str)
            return buffer.ReadString(str.Address, str.MaxLength ?? 0).TrimEnd();
        return "";
    }
}
