using System;
using System.Buffers.Binary;

namespace CasualtiesCoop.Networking;

internal static class GameplayWire
{
    internal static bool IsWorldMessage(MessageKind kind) =>
        kind is >= MessageKind.CharacterPose and <= MessageKind.SandboxAlcohol;

    internal static byte[] NewFrame(MessageKind kind, Guid session, int size)
    {
        byte[] frame = new byte[size];
        frame[0] = (byte)kind;
        session.TryWriteBytes(frame.AsSpan(1));
        return frame;
    }

    internal static bool HasHeader(ReadOnlySpan<byte> message, MessageKind kind, Guid session) =>
        message.Length >= 17 && message[0] == (byte)kind && new Guid(message.Slice(1, 16)) == session;

    internal static void WriteFloat(Span<byte> data, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(data, BitConverter.SingleToInt32Bits(value));

    internal static float ReadFloat(ReadOnlySpan<byte> data) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data));

    internal static bool InRange(float value, float limit) => float.IsFinite(value) && Math.Abs(value) <= limit;
    internal static bool IsCoordinate(float value) => InRange(value, 32768f);
}
