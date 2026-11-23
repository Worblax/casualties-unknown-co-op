using System;
using System.Buffers.Binary;
using System.Text;
using CasualtiesCoop.Networking;
using static CasualtiesCoop.Networking.GameplayWire;

namespace CasualtiesCoop.Gameplay.Audio;

internal readonly struct CharacterSound
{
    internal readonly ulong Player;
    internal readonly uint Sequence;
    internal readonly float X;
    internal readonly float Y;
    internal readonly float Volume;
    internal readonly float Pitch;
    internal readonly string Clip;

    internal CharacterSound(ulong player, uint sequence, float x, float y, float volume, float pitch, string clip)
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

internal static class CharacterSoundWire
{
    private const int SoundHeaderSize = 45;
    private const int MaxClipLength = 100;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static byte[]? Write(Guid session, CharacterSound sound)
    {
        if (!IsClipKey(sound.Clip))
        {
            return null;
        }

        byte[] name = Utf8.GetBytes(sound.Clip);
        byte[] message = NewFrame(MessageKind.CharacterSound, session, SoundHeaderSize + name.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), sound.Player);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(25), sound.Sequence);
        WriteFloat(message.AsSpan(29), sound.X);
        WriteFloat(message.AsSpan(33), sound.Y);
        WriteFloat(message.AsSpan(37), sound.Volume);
        WriteFloat(message.AsSpan(41), sound.Pitch);
        name.CopyTo(message, SoundHeaderSize);
        return message;
    }

    internal static bool TryRead(ReadOnlySpan<byte> message, Guid session, out CharacterSound sound)
    {
        sound = default;
        int length = message.Length - SoundHeaderSize;
        if (length is < 1 or > MaxClipLength || !HasHeader(message, MessageKind.CharacterSound, session))
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

        sound = new CharacterSound(player, sequence, x, y, volume, pitch, clip);
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
}
