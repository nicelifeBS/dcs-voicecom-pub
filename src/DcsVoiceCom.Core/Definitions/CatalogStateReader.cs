using DcsVoiceCom.DcsBios;

namespace DcsVoiceCom.Definitions;

/// <summary>Reads named outputs from the live state buffer using the catalog's addresses.</summary>
public interface IStateReader
{
    /// <summary>True while export frames are arriving.</summary>
    bool IsLive { get; }

    bool TryReadInteger(string identifier, out int value);

    bool TryReadString(string identifier, out string value);
}

public sealed class CatalogStateReader : IStateReader
{
    private readonly StateBuffer _buffer;
    private readonly Func<ControlCatalog> _catalog;
    private readonly Func<bool> _isLive;

    public CatalogStateReader(StateBuffer buffer, Func<ControlCatalog> catalog, Func<bool>? isLive = null)
    {
        _buffer = buffer;
        _catalog = catalog;
        _isLive = isLive ?? (() => true);
    }

    public bool IsLive => _isLive();

    public bool TryReadInteger(string identifier, out int value)
    {
        var output = _catalog().Find(identifier)?.PrimaryIntegerOutput;
        if (output is null)
        {
            value = 0;
            return false;
        }
        value = _buffer.ReadInteger(output.Address, output.Mask ?? 0xFFFF, output.ShiftBy ?? 0);
        return true;
    }

    public bool TryReadString(string identifier, out string value)
    {
        var output = _catalog().Find(identifier)?.PrimaryStringOutput;
        if (output is null)
        {
            value = "";
            return false;
        }
        value = _buffer.ReadString(output.Address, output.MaxLength ?? 0);
        return true;
    }
}
