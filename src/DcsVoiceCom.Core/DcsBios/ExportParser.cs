namespace DcsVoiceCom.DcsBios;

/// <summary>
/// Byte-level parser for the DCS-BIOS export stream. A frame starts with the sync
/// sequence 0x55 0x55 0x55 0x55, followed by write blocks of
/// [address u16 LE][length u16 LE][length bytes of data]. The parser keeps its state
/// across packets, so a block split over two UDP datagrams is handled.
/// </summary>
public sealed class ExportParser
{
    private enum State { WaitForSync, AddressLow, AddressHigh, CountLow, CountHigh, Data }

    private readonly StateBuffer _buffer;
    private State _state = State.WaitForSync;
    private int _syncCount;
    private int _address;
    private int _count;

    public ExportParser(StateBuffer buffer) => _buffer = buffer;

    /// <summary>Raised when a sync sequence arrives, i.e. the previous frame is complete.</summary>
    public event Action? FrameSync;

    public void Feed(ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length)
        {
            bool synced;
            if (_state == State.Data)
            {
                // Copy the whole run of block data in one write instead of byte by byte.
                var run = data.Slice(i, Math.Min(_count, data.Length - i));
                var end = run.Length;
                synced = false;
                for (var j = 0; j < run.Length; j++)
                {
                    if (!TrackSync(run[j])) continue;
                    end = j + 1;
                    synced = true;
                    break;
                }
                _buffer.Write(_address, run[..end]);
                _address += end;
                _count -= end;
                if (_count == 0) _state = State.AddressLow;
                i += end;
            }
            else
            {
                var b = data[i++];
                Process(b);
                synced = TrackSync(b);
            }

            if (synced)
            {
                _state = State.AddressLow;
                FrameSync?.Invoke();
            }
        }
    }

    /// <summary>
    /// Sync detection runs alongside the block parser, as the stream may be joined mid-frame. True when
    /// <paramref name="b"/> completes a sync sequence.
    /// </summary>
    private bool TrackSync(byte b)
    {
        _syncCount = b == 0x55 ? _syncCount + 1 : 0;
        if (_syncCount < 4) return false;
        _syncCount = 0;
        return true;
    }

    private void Process(byte b)
    {
        switch (_state)
        {
            case State.WaitForSync:
                break;
            case State.AddressLow:
                _address = b;
                _state = State.AddressHigh;
                break;
            case State.AddressHigh:
                _address |= b << 8;
                _state = _address == 0x5555 ? State.WaitForSync : State.CountLow;
                break;
            case State.CountLow:
                _count = b;
                _state = State.CountHigh;
                break;
            case State.CountHigh:
                _count |= b << 8;
                _state = _count == 0 ? State.AddressLow : State.Data;
                break;
            case State.Data: // handled in bulk by Feed
                break;
        }
    }
}
