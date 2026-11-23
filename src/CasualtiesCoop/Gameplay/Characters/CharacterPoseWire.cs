using System;
using System.Buffers.Binary;
using CasualtiesCoop.Networking;
using static CasualtiesCoop.Networking.GameplayWire;

namespace CasualtiesCoop.Gameplay.Characters;

internal readonly struct CharacterPose
{
    internal readonly ulong Player;
    internal readonly uint Sequence;
    internal readonly float X;
    internal readonly float Y;
    internal readonly float VelocityX;
    internal readonly float VelocityY;
    internal readonly bool FacingRight;
    internal readonly bool Standing;
    internal readonly LimbPose[] Limbs;

    internal CharacterPose(ulong player, uint sequence, float x, float y, float velocityX = 0, float velocityY = 0,
        bool facingRight = true, bool standing = true, LimbPose[]? limbs = null)
    {
        Player = player;
        Sequence = sequence;
        X = x;
        Y = y;
        VelocityX = velocityX;
        VelocityY = velocityY;
        FacingRight = facingRight;
        Standing = standing;
        Limbs = limbs ?? Array.Empty<LimbPose>();
    }
}

internal readonly struct LimbPose
{
    internal readonly float X;
    internal readonly float Y;
    internal readonly float Angle;
    internal readonly float ScaleX;
    internal readonly float ScaleY;
    internal readonly bool Visible;

    internal LimbPose(float x, float y, float angle, float scaleX, float scaleY, bool visible)
    {
        X = x;
        Y = y;
        Angle = angle;
        ScaleX = scaleX;
        ScaleY = scaleY;
        Visible = visible;
    }
}

internal static class CharacterPoseWire
{
    internal const int MaxLimbs = 24;
    private const int PositionSize = 51;
    private const int LimbSize = 20;

    internal static byte[] Write(Guid session, CharacterPose position)
    {
        int count = Math.Min(position.Limbs.Length, MaxLimbs);
        byte[] message = NewFrame(MessageKind.CharacterPose, session, PositionSize + count * LimbSize);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), position.Player);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(25), position.Sequence);
        WriteFloat(message.AsSpan(29), position.X);
        WriteFloat(message.AsSpan(33), position.Y);
        WriteFloat(message.AsSpan(37), position.VelocityX);
        WriteFloat(message.AsSpan(41), position.VelocityY);
        message[45] = (byte)((position.FacingRight ? 1 : 0) | (position.Standing ? 2 : 0));
        message[46] = (byte)count;
        uint visible = 0;
        for (int index = 0; index < count; index++)
        {
            LimbPose limb = position.Limbs[index];
            Span<byte> data = message.AsSpan(PositionSize + index * LimbSize);
            WriteFloat(data, limb.X);
            WriteFloat(data.Slice(4), limb.Y);
            WriteFloat(data.Slice(8), limb.Angle);
            WriteFloat(data.Slice(12), limb.ScaleX);
            WriteFloat(data.Slice(16), limb.ScaleY);
            if (limb.Visible)
            {
                visible |= 1u << index;
            }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(47), visible);
        return message;
    }

    internal static bool TryRead(ReadOnlySpan<byte> message, Guid session, out CharacterPose position)
    {
        position = default;
        if (message.Length < PositionSize || !HasHeader(message, MessageKind.CharacterPose, session))
        {
            return false;
        }

        ulong player = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(17));
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(25));
        float x = ReadFloat(message.Slice(29));
        float y = ReadFloat(message.Slice(33));
        float vx = ReadFloat(message.Slice(37));
        float vy = ReadFloat(message.Slice(41));
        int count = message[46];
        if (player == 0 || !IsCoordinate(x) || !IsCoordinate(y) || !InRange(vx, 1000) || !InRange(vy, 1000) ||
            message[45] > 3 || count > MaxLimbs || message.Length != PositionSize + count * LimbSize)
        {
            return false;
        }

        uint visible = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(47));
        LimbPose[] limbs = new LimbPose[count];
        for (int index = 0; index < limbs.Length; index++)
        {
            ReadOnlySpan<byte> data = message.Slice(PositionSize + index * LimbSize);
            float lx = ReadFloat(data);
            float ly = ReadFloat(data.Slice(4));
            float angle = ReadFloat(data.Slice(8));
            float sx = ReadFloat(data.Slice(12));
            float sy = ReadFloat(data.Slice(16));
            if (!InRange(lx, 256) || !InRange(ly, 256) || !InRange(angle, 360) ||
                !InRange(sx, 8) || !InRange(sy, 8) || sx < 0 || sy < 0)
            {
                return false;
            }
            limbs[index] = new LimbPose(lx, ly, angle, sx, sy, (visible & (1u << index)) != 0);
        }

        position = new CharacterPose(player, sequence, x, y, vx, vy, (message[45] & 1) != 0, (message[45] & 2) != 0, limbs);
        return true;
    }
}
