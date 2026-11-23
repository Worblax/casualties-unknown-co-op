using System;
using System.Buffers.Binary;

namespace CasualtiesCoop.Networking;

internal readonly struct SandboxPosition
{
    internal readonly ulong Player;
    internal readonly uint Sequence;
    internal readonly float X;
    internal readonly float Y;

    internal SandboxPosition(ulong player, uint sequence, float x, float y)
    {
        Player = player;
        Sequence = sequence;
        X = x;
        Y = y;
    }
}

internal static class SandboxWire
{
    private const int PositionSize = 37;
    private const float MaxCoordinate = 32768f;

    internal static byte[] WritePosition(Guid session, SandboxPosition position)
    {
        byte[] message = new byte[PositionSize];
        message[0] = (byte)RoomFrame.SandboxPosition;
        session.TryWriteBytes(message.AsSpan(1));
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), position.Player);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(25), position.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(29), BitConverter.SingleToInt32Bits(position.X));
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(33), BitConverter.SingleToInt32Bits(position.Y));
        return message;
    }

    internal static bool TryReadPosition(ReadOnlySpan<byte> message, Guid session, out SandboxPosition position)
    {
        position = default;
        if (message.Length != PositionSize || message[0] != (byte)RoomFrame.SandboxPosition ||
            new Guid(message.Slice(1, 16)) != session)
        {
            return false;
        }

        ulong player = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(17));
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(25));
        float x = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(message.Slice(29)));
        float y = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(message.Slice(33)));
        if (player == 0 || !float.IsFinite(x) || !float.IsFinite(y) ||
            Math.Abs(x) > MaxCoordinate || Math.Abs(y) > MaxCoordinate)
        {
            return false;
        }

        position = new SandboxPosition(player, sequence, x, y);
        return true;
    }
}
