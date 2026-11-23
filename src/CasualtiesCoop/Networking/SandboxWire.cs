using System;
using System.Buffers.Binary;
using System.Text;

namespace CasualtiesCoop.Networking;

internal readonly struct SandboxPosition
{
    internal readonly ulong Player;
    internal readonly uint Sequence;
    internal readonly float X;
    internal readonly float Y;
    internal readonly float VelocityX;
    internal readonly float VelocityY;
    internal readonly bool FacingRight;
    internal readonly bool Standing;
    internal readonly SandboxLimb[] Limbs;

    internal SandboxPosition(ulong player, uint sequence, float x, float y, float velocityX = 0, float velocityY = 0,
        bool facingRight = true, bool standing = true, SandboxLimb[]? limbs = null)
    {
        Player = player;
        Sequence = sequence;
        X = x;
        Y = y;
        VelocityX = velocityX;
        VelocityY = velocityY;
        FacingRight = facingRight;
        Standing = standing;
        Limbs = limbs ?? Array.Empty<SandboxLimb>();
    }
}

internal readonly struct SandboxLimb
{
    internal readonly float X;
    internal readonly float Y;
    internal readonly float Angle;
    internal readonly float ScaleX;
    internal readonly float ScaleY;
    internal readonly bool Visible;

    internal SandboxLimb(float x, float y, float angle, float scaleX, float scaleY, bool visible)
    {
        X = x;
        Y = y;
        Angle = angle;
        ScaleX = scaleX;
        ScaleY = scaleY;
        Visible = visible;
    }
}

internal readonly struct SandboxSound
{
    internal readonly ulong Player;
    internal readonly uint Sequence;
    internal readonly float X;
    internal readonly float Y;
    internal readonly float Volume;
    internal readonly float Pitch;
    internal readonly string Clip;

    internal SandboxSound(ulong player, uint sequence, float x, float y, float volume, float pitch, string clip)
    {
        Player = player;
        Sequence = sequence;
        X = x;
        Y = y;
        Volume = volume;
        Pitch = pitch;
        Clip = clip;
    }
}

internal static class SandboxWire
{
    private const int PositionSize = 51;
    private const int LimbSize = 20;
    internal const int MaxLimbs = 24;
    private const int SoundHeaderSize = 45;
    private const int MaxClipLength = 100;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static byte[] WritePosition(Guid session, SandboxPosition position)
    {
        int count = Math.Min(position.Limbs.Length, MaxLimbs);
        byte[] message = NewFrame(RoomFrame.SandboxPosition, session, PositionSize + count * LimbSize);
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
            SandboxLimb limb = position.Limbs[index];
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

    internal static bool TryReadPosition(ReadOnlySpan<byte> message, Guid session, out SandboxPosition position)
    {
        position = default;
        if (message.Length < PositionSize || !HasHeader(message, RoomFrame.SandboxPosition, session))
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
        SandboxLimb[] limbs = new SandboxLimb[count];
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
            limbs[index] = new SandboxLimb(lx, ly, angle, sx, sy, (visible & (1u << index)) != 0);
        }

        position = new SandboxPosition(player, sequence, x, y, vx, vy, (message[45] & 1) != 0, (message[45] & 2) != 0, limbs);
        return true;
    }

    internal static byte[]? WriteSound(Guid session, SandboxSound sound)
    {
        if (!IsClipKey(sound.Clip))
        {
            return null;
        }

        byte[] name = Utf8.GetBytes(sound.Clip);
        byte[] message = NewFrame(RoomFrame.SandboxSound, session, SoundHeaderSize + name.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), sound.Player);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(25), sound.Sequence);
        WriteFloat(message.AsSpan(29), sound.X);
        WriteFloat(message.AsSpan(33), sound.Y);
        WriteFloat(message.AsSpan(37), sound.Volume);
        WriteFloat(message.AsSpan(41), sound.Pitch);
        name.CopyTo(message, SoundHeaderSize);
        return message;
    }

    internal static bool TryReadSound(ReadOnlySpan<byte> message, Guid session, out SandboxSound sound)
    {
        sound = default;
        int length = message.Length - SoundHeaderSize;
        if (length is < 1 or > MaxClipLength || !HasHeader(message, RoomFrame.SandboxSound, session))
        {
            return false;
        }

        ulong player = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(17));
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(25));
        float x = ReadFloat(message.Slice(29));
        float y = ReadFloat(message.Slice(33));
        float volume = ReadFloat(message.Slice(37));
        float pitch = ReadFloat(message.Slice(41));
        if (player == 0 || !IsCoordinate(x) || !IsCoordinate(y) || !InRange(volume, 1) || volume < 0 ||
            !InRange(pitch, 3) || pitch < 0.05f)
        {
            return false;
        }

        string clip;
        try { clip = Utf8.GetString(message.Slice(SoundHeaderSize)); }
        catch (DecoderFallbackException) { return false; }
        if (!IsClipKey(clip))
        {
            return false;
        }

        sound = new SandboxSound(player, sequence, x, y, volume, pitch, clip);
        return true;
    }

    private static bool IsClipKey(string clip)
    {
        if (clip.Length is < 1 or > MaxClipLength || clip[0] == '/' || clip.Contains("//"))
        {
            return false;
        }

        foreach (char c in clip)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '/' or ' ' or '@'))
            {
                return false;
            }
        }
        return true;
    }

    internal static byte[] NewFrame(RoomFrame kind, Guid session, int size)
    {
        byte[] frame = new byte[size];
        frame[0] = (byte)kind;
        session.TryWriteBytes(frame.AsSpan(1));
        return frame;
    }

    internal static bool HasHeader(ReadOnlySpan<byte> message, RoomFrame kind, Guid session) =>
        message.Length >= 17 && message[0] == (byte)kind && new Guid(message.Slice(1, 16)) == session;

    internal static void WriteFloat(Span<byte> data, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(data, BitConverter.SingleToInt32Bits(value));

    internal static float ReadFloat(ReadOnlySpan<byte> data) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data));

    internal static bool InRange(float value, float limit) => float.IsFinite(value) && Math.Abs(value) <= limit;
    internal static bool IsCoordinate(float value) => InRange(value, 32768f);
}
