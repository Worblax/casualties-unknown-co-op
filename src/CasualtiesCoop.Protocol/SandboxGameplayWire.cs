using System;
using System.Buffers.Binary;
using static CasualtiesCoop.Networking.GameplayWire;

namespace CasualtiesCoop.Networking;

internal static class SandboxGameplayWire
{
    internal static byte[] WriteClaw(Guid session, ulong player, uint sequence, float x, float y, float angle, bool right)
    {
        byte[] frame = NewFrame(MessageKind.ClawEffect, session, 42);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(17), player);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(25), sequence);
        WriteFloat(frame.AsSpan(29), x);
        WriteFloat(frame.AsSpan(33), y);
        WriteFloat(frame.AsSpan(37), angle);
        frame[41] = right ? (byte)1 : (byte)0;
        return frame;
    }

    internal static bool TryReadClaw(ReadOnlySpan<byte> frame, Guid session,
        out (ulong Player, uint Sequence, float X, float Y, float Angle, bool Right) effect)
    {
        effect = default;
        if (frame.Length != 42 || !HasHeader(frame, MessageKind.ClawEffect, session) || frame[41] > 1)
        {
            return false;
        }
        effect = (BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(17)), BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(25)),
            ReadFloat(frame.Slice(29)), ReadFloat(frame.Slice(33)), ReadFloat(frame.Slice(37)), frame[41] != 0);
        return effect.Player != 0 && effect.Sequence != 0 && IsCoordinate(effect.X) && IsCoordinate(effect.Y) &&
            effect.Angle is >= 0 and <= 360;
    }

    internal static byte[] WriteTerrainHit(Guid session, int x, int y, ushort block, uint sequence, float damage, byte flags = 0)
    {
        byte[] frame = NewFrame(MessageKind.TerrainHit, session, 32);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(17), checked((ushort)x));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(19), checked((ushort)y));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(21), block);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(23), sequence);
        WriteFloat(frame.AsSpan(27), damage);
        frame[31] = flags;
        return frame;
    }

    internal static bool TryReadTerrainHit(ReadOnlySpan<byte> frame, Guid session,
        out (int X, int Y, ushort Block, uint Sequence, float Damage, byte Flags) tile)
    {
        tile = default;
        if (frame.Length != 32 || !HasHeader(frame, MessageKind.TerrainHit, session) || frame[31] > 3)
        {
            return false;
        }
        tile = (BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(17)), BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(19)),
            BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(21)), BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(23)),
            ReadFloat(frame.Slice(27)), frame[31]);
        return tile.Sequence != 0 && tile.Damage is > 0 and <= 100000f && tile.Block != 0;
    }

    internal static byte[] WriteAlcohol(Guid session, uint operation, float x, float y, byte slot)
    {
        byte[] frame = NewFrame(MessageKind.SandboxAlcohol, session, 30);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(17), operation);
        WriteFloat(frame.AsSpan(21), x);
        WriteFloat(frame.AsSpan(25), y);
        frame[29] = slot;
        return frame;
    }

    internal static bool TryReadAlcohol(ReadOnlySpan<byte> frame, Guid session,
        out (uint Operation, float X, float Y, byte Slot) request)
    {
        request = default;
        if (frame.Length != 30 || !HasHeader(frame, MessageKind.SandboxAlcohol, session))
        {
            return false;
        }
        request = (BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(17)), ReadFloat(frame.Slice(21)),
            ReadFloat(frame.Slice(25)), frame[29]);
        return request.Operation != 0 && IsCoordinate(request.X) && IsCoordinate(request.Y);
    }
}
