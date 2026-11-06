using System.Text;
using DcsVoiceCom.DcsBios;

namespace DcsVoiceCom.Tests;

public class ExportParserTests
{
    internal static byte[] Frame(params (int Address, byte[] Data)[] writes)
    {
        var bytes = new List<byte> { 0x55, 0x55, 0x55, 0x55 };
        foreach (var (address, data) in writes)
        {
            bytes.AddRange(BitConverter.GetBytes((ushort)address));
            bytes.AddRange(BitConverter.GetBytes((ushort)data.Length));
            bytes.AddRange(data);
        }
        return bytes.ToArray();
    }

    internal static byte[] Word(int value) => BitConverter.GetBytes((ushort)value);

    internal static byte[] Text(string value, int length)
    {
        var data = new byte[length];
        Encoding.ASCII.GetBytes(value).CopyTo(data, 0);
        return data;
    }

    [Fact]
    public void Writes_blocks_into_the_state_buffer()
    {
        var buffer = new StateBuffer();
        var parser = new ExportParser(buffer);

        parser.Feed(Frame((0x1200, Word(0b0110_0000_0000_0100)), (0x1210, Word(0xABCD))));

        Assert.Equal(1, buffer.ReadInteger(0x1200, 0x0004, 2));
        Assert.Equal(3, buffer.ReadInteger(0x1200, 0x6000, 13));
        Assert.Equal(0xABCD, buffer.ReadWord(0x1210));
    }

    [Fact]
    public void Handles_blocks_split_across_packets_and_raises_frame_sync()
    {
        var buffer = new StateBuffer();
        var parser = new ExportParser(buffer);
        var frames = 0;
        parser.FrameSync += () => frames++;

        var data = Frame((0x0000, Text("F-14B", 24)), (0x1400, Word(42)));
        parser.Feed(data.AsSpan(0, 7));
        parser.Feed(data.AsSpan(7, 20));
        parser.Feed(data.AsSpan(27));

        Assert.Equal(1, frames);
        Assert.Equal("F-14B", buffer.ReadString(0x0000, 24));
        Assert.Equal(42, buffer.ReadWord(0x1400));
    }

    [Fact]
    public void A_sync_inside_block_data_starts_a_new_frame()
    {
        var buffer = new StateBuffer();
        var parser = new ExportParser(buffer);
        var frames = 0;
        parser.FrameSync += () => frames++;

        // The 6-byte block is cut short by a sync after its 5th byte; a new block follows.
        parser.Feed([.. Frame(), 0x00, 0x13, 0x06, 0x00, 0x01, 0x55, 0x55, 0x55, 0x55, .. Frame((0x1500, Word(9)))[4..]]);

        Assert.Equal(2, frames);
        Assert.Equal(0x5501, buffer.ReadWord(0x1300));
        Assert.Equal(0x5555, buffer.ReadWord(0x1302));
        Assert.Equal(0x0055, buffer.ReadWord(0x1304));
        Assert.Equal(0, buffer.ReadWord(0x1306));
        Assert.Equal(9, buffer.ReadWord(0x1500));
    }

    [Fact]
    public void Ignores_data_before_the_first_sync()
    {
        var buffer = new StateBuffer();
        var parser = new ExportParser(buffer);

        parser.Feed([0x00, 0x14, 0x02, 0x00, 0x07, 0x00]);
        parser.Feed(Frame((0x1500, Word(9))));

        Assert.Equal(0, buffer.ReadWord(0x1400));
        Assert.Equal(9, buffer.ReadWord(0x1500));
    }

    [Fact]
    public async Task Connection_reports_aircraft_changes_on_frame_sync()
    {
        await using var connection = new DcsBiosConnection();
        var changes = new List<string>();
        connection.AircraftChanged += changes.Add;

        connection.Feed(Frame((0x0000, Text("F-14B", 24))));
        connection.Feed(Frame((0x0000, Text("F-14B", 24))));
        connection.Feed(Frame((0x0000, Text("FA-18C_hornet", 24))));
        connection.Feed(Frame());

        Assert.Equal(["F-14B", "FA-18C_hornet"], changes);
        Assert.Equal("FA-18C_hornet", connection.Aircraft);
    }

    [Fact]
    public async Task Connection_keeps_receiving_after_a_handler_throws()
    {
        var port = Random.Shared.Next(40000, 50000);
        await using var connection = new DcsBiosConnection(new DcsBiosEndpoints { ExportPort = port });
        var errors = new List<Exception>();
        connection.ReceiveError += errors.Add;
        connection.AircraftChanged += _ => throw new InvalidOperationException("handler failed");
        connection.Start();

        using var sender = new System.Net.Sockets.UdpClient();
        var target = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port);
        await sender.SendAsync(Frame((0x0000, Text("F-14B", 24))), target);
        await sender.SendAsync(Frame((0x1400, Word(42))), target);
        await sender.SendAsync(Frame(), target);

        for (var i = 0; i < 100 && connection.State.ReadWord(0x1400) != 42; i++) await Task.Delay(20);
        Assert.Equal(42, connection.State.ReadWord(0x1400));
        Assert.Equal("handler failed", Assert.Single(errors).Message);
    }
}
