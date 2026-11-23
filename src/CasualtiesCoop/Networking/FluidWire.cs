using System;
using System.Buffers.Binary;

namespace CasualtiesCoop.Networking;

internal static class FluidWire
{
    internal const int ChunkSize = 32;
    internal const int PackedSize = ChunkSize * ChunkSize * 3 / 8;
    private const int HeaderSize = 25;
    private const int DrinkSize = 30;
    private const int PushSize = 22;

    internal static byte[] WriteChunk(Guid session, int x, int y, uint version, ReadOnlySpan<byte> cells)
    {
        byte[] frame = SandboxWire.NewFrame(RoomFrame.FluidChunk, session, HeaderSize + PackedSize);
        WriteCell(frame.AsSpan(17), x, y);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(21), version);
        cells.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    internal static bool TryReadChunk(byte[] frame, Guid session, out int x, out int y, out uint version, out int offset)
    {
        x = y = offset = 0;
        version = 0;
        if (frame.Length != HeaderSize + PackedSize || !SandboxWire.HasHeader(frame, RoomFrame.FluidChunk, session))
        {
            return false;
        }

        ReadCell(frame.AsSpan(17), out x, out y);
        version = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(21));
        offset = HeaderSize;
        return version != 0;
    }

    internal static void Pack(Span<byte> packed, int index, byte value)
    {
        int bit = index * 3;
        int shift = bit & 7;
        packed[bit >> 3] |= (byte)((value & 7) << shift);
        if (shift > 5)
        {
            packed[(bit >> 3) + 1] |= (byte)((value & 7) >> (8 - shift));
        }
    }

    internal static byte Unpack(ReadOnlySpan<byte> packed, int index)
    {
        int bit = index * 3;
        int shift = bit & 7;
        int value = packed[bit >> 3] >> shift;
        if (shift > 5)
        {
            value |= packed[(bit >> 3) + 1] << (8 - shift);
        }
        return (byte)(value & 7);
    }

    internal static byte[] WriteDrink(RoomFrame frame, Guid session, ulong operation, int x, int y, byte liquid)
    {
        byte[] message = SandboxWire.NewFrame(frame, session, DrinkSize);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), operation);
        WriteCell(message.AsSpan(25), x, y);
        message[29] = liquid;
        return message;
    }

    internal static bool TryReadDrink(byte[] message, RoomFrame frame, Guid session, out ulong operation,
        out int x, out int y, out byte liquid)
    {
        operation = 0;
        x = y = 0;
        liquid = 0;
        if (message.Length != DrinkSize || !SandboxWire.HasHeader(message, frame, session))
        {
            return false;
        }

        operation = BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(17));
        ReadCell(message.AsSpan(25), out x, out y);
        liquid = message[29];
        return operation != 0 && liquid <= 7;
    }

    internal static byte[] WritePush(Guid session, int x, int y, byte direction)
    {
        byte[] message = SandboxWire.NewFrame(RoomFrame.FluidPush, session, PushSize);
        WriteCell(message.AsSpan(17), x, y);
        message[21] = direction;
        return message;
    }

    internal static bool TryReadPush(byte[] message, Guid session, out int x, out int y, out byte direction)
    {
        x = y = 0;
        direction = 0;
        if (message.Length != PushSize || !SandboxWire.HasHeader(message, RoomFrame.FluidPush, session))
        {
            return false;
        }

        ReadCell(message.AsSpan(17), out x, out y);
        direction = message[21];
        return direction <= 2;
    }

    private static void WriteCell(Span<byte> value, int x, int y)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(value, checked((ushort)x));
        BinaryPrimitives.WriteUInt16LittleEndian(value.Slice(2), checked((ushort)y));
    }

    private static void ReadCell(ReadOnlySpan<byte> value, out int x, out int y)
    {
        x = BinaryPrimitives.ReadUInt16LittleEndian(value);
        y = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(2));
    }
}
