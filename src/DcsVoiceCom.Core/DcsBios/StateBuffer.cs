using System.Text;

namespace DcsVoiceCom.DcsBios;

/// <summary>
/// The 64 KB DCS-BIOS export address space. Values are 16-bit little-endian words;
/// the byte at an even address is the low byte of that word.
/// </summary>
public sealed class StateBuffer
{
    public const int Size = 0x10000;

    private readonly byte[] _data = new byte[Size];
    private readonly object _lock = new();

    public void Write(int address, ReadOnlySpan<byte> bytes)
    {
        if (address < 0 || address >= Size) return;
        var length = Math.Min(bytes.Length, Size - address);
        lock (_lock)
        {
            bytes[..length].CopyTo(_data.AsSpan(address));
        }
    }

    public ushort ReadWord(int address)
    {
        if (address < 0 || address + 1 >= Size) return 0;
        lock (_lock)
        {
            return (ushort)(_data[address] | (_data[address + 1] << 8));
        }
    }

    public int ReadInteger(int address, int mask, int shiftBy) => (ReadWord(address) & mask) >> shiftBy;

    /// <summary>Reads a string of up to <paramref name="maxLength"/> bytes, stopping at the first NUL.</summary>
    public string ReadString(int address, int maxLength)
    {
        if (address < 0 || address >= Size || maxLength <= 0) return string.Empty;
        var length = Math.Min(maxLength, Size - address);
        lock (_lock)
        {
            var span = _data.AsSpan(address, length);
            var end = span.IndexOf((byte)0);
            if (end >= 0) span = span[..end];
            return Encoding.Latin1.GetString(span).TrimEnd();
        }
    }
}
