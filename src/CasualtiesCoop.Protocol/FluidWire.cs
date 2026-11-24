using CasualtiesCoop.Networking;
using System;
using System.Buffers.Binary;

namespace CasualtiesCoop.Gameplay.Fluids;

internal static class FluidWire
{
    private const int DrinkSize = 30;
    private const int PushSize = 22;

    internal static byte[] WriteDrink(MessageKind frame, Guid session, ulong operation, int x, int y, byte liquid)
    {
        byte[] message = GameplayWire.NewFrame(frame, session, DrinkSize);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), operation);
        WriteCell(message.AsSpan(25), x, y);
        message[29] = liquid;
        return message;
    }

    internal static bool TryReadDrink(byte[] message, MessageKind frame, Guid session, out ulong operation,
        out int x, out int y, out byte liquid)
    {
        operation = 0;
        x = y = 0;
        liquid = 0;
        if (message.Length != DrinkSize || !GameplayWire.HasHeader(message, frame, session))
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
        byte[] message = GameplayWire.NewFrame(MessageKind.FluidPush, session, PushSize);
        WriteCell(message.AsSpan(17), x, y);
        message[21] = direction;
        return message;
    }

    internal static bool TryReadPush(byte[] message, Guid session, out int x, out int y, out byte direction)
    {
        x = y = 0;
        direction = 0;
        if (message.Length != PushSize || !GameplayWire.HasHeader(message, MessageKind.FluidPush, session))
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
