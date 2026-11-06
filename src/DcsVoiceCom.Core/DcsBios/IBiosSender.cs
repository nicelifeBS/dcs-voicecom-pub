namespace DcsVoiceCom.DcsBios;

/// <summary>Sends DCS-BIOS input commands ("CONTROL_ID ARGUMENT").</summary>
public interface IBiosSender
{
    Task SendAsync(string controlId, string argument, CancellationToken cancellationToken = default);
}
