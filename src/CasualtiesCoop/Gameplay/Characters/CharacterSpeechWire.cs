using System;
using System.Buffers.Binary;
using System.Text;
using CasualtiesCoop.Networking;

namespace CasualtiesCoop.Gameplay.Characters;

internal readonly struct CharacterSpeech
{
    internal readonly ulong Player;
    internal readonly uint Sequence;
    internal readonly float TimeMultiplier;
    internal readonly string Text;

    internal CharacterSpeech(ulong player, uint sequence, float timeMultiplier, string text)
    {
        Player = player;
        Sequence = sequence;
        TimeMultiplier = timeMultiplier;
        Text = text;
    }
}

internal static class CharacterSpeechWire
{
    internal const float MinMultiplier = 0.05f;
    internal const float MaxMultiplier = 4f;
    private const int HeaderSize = 33;
    private const int MaxTextBytes = 512;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static byte[]? Write(Guid session, CharacterSpeech line)
    {
        if (line.Player == 0 || !ValidMultiplier(line.TimeMultiplier) || line.Text.Length > MaxTextBytes ||
            line.Text.IndexOf('\0') >= 0)
        {
            return null;
        }

        int length;
        try { length = Utf8.GetByteCount(line.Text); }
        catch (EncoderFallbackException) { return null; }
        if (length > MaxTextBytes) return null;

        byte[] frame = GameplayWire.NewFrame(MessageKind.CharacterSpeech, session, HeaderSize + length);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(17), line.Player);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(25), line.Sequence);
        GameplayWire.WriteFloat(frame.AsSpan(29), line.TimeMultiplier);
        Utf8.GetBytes(line.Text.AsSpan(), frame.AsSpan(HeaderSize));
        return frame;
    }

    internal static bool TryRead(ReadOnlySpan<byte> frame, Guid session, out CharacterSpeech line)
    {
        line = default;
        if (frame.Length < HeaderSize || frame.Length > HeaderSize + MaxTextBytes ||
            !GameplayWire.HasHeader(frame, MessageKind.CharacterSpeech, session))
        {
            return false;
        }

        ulong player = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(17));
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(25));
        float multiplier = GameplayWire.ReadFloat(frame.Slice(29));
        if (player == 0 || !ValidMultiplier(multiplier)) return false;

        string text;
        try { text = Utf8.GetString(frame.Slice(HeaderSize)); }
        catch (DecoderFallbackException) { return false; }
        if (text.IndexOf('\0') >= 0) return false;

        line = new CharacterSpeech(player, sequence, multiplier, text);
        return true;
    }

    private static bool ValidMultiplier(float multiplier) =>
        float.IsFinite(multiplier) && multiplier is >= MinMultiplier and <= MaxMultiplier;
}
