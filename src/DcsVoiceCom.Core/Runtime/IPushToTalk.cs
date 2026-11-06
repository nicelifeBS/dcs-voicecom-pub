namespace DcsVoiceCom.Runtime;

/// <summary>A push-to-talk source, e.g. a HOTAS button read through DirectInput.</summary>
public interface IPushToTalk : IDisposable
{
    event Action? Pressed;
    event Action? Released;
    void Start();
}
