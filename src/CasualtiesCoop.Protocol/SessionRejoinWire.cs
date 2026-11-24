using CasualtiesCoop.Networking;
using System;
using System.Buffers.Binary;
using CasualtiesCoop.Session;

namespace CasualtiesCoop.Gameplay.Recovery;

internal readonly struct SessionRejoinState
{
    internal readonly RoomLaunchPlan Plan;
    internal readonly Guid Attempt;
    internal readonly uint PoseSequence;
    internal readonly uint SoundSequence;
    internal readonly uint LightSequence;
    internal readonly ulong ItemOperation;
    internal readonly ulong DrinkOperation;
    internal readonly float X;
    internal readonly float Y;
    internal readonly bool HasPosition;

    internal SessionRejoinState(RoomLaunchPlan plan, Guid attempt, uint poseSequence, uint soundSequence, uint lightSequence,
        ulong itemOperation, ulong drinkOperation, float x, float y, bool hasPosition)
    {
        Plan = plan;
        Attempt = attempt;
        PoseSequence = poseSequence;
        SoundSequence = soundSequence;
        LightSequence = lightSequence;
        ItemOperation = itemOperation;
        DrinkOperation = drinkOperation;
        X = x;
        Y = y;
        HasPosition = hasPosition;
    }
}

internal static class SessionRejoinWire
{
    private const int ReferenceSize = 33;
    private const int StateHeaderSize = 70;

    internal static byte[] WriteReference(MessageKind frame, Guid session, Guid attempt)
    {
        byte[] message = GameplayWire.NewFrame(frame, session, ReferenceSize);
        attempt.TryWriteBytes(message.AsSpan(17));
        return message;
    }

    internal static Guid ReadReference(ReadOnlySpan<byte> message, MessageKind frame, Guid session) =>
        message.Length == ReferenceSize && GameplayWire.HasHeader(message, frame, session) ? new Guid(message.Slice(17)) : Guid.Empty;

    internal static byte[]? WriteState(SessionRejoinState state)
    {
        if (RoomWire.WritePlan(state.Plan) is not { } plan)
        {
            return null;
        }

        byte[] message = GameplayWire.NewFrame(MessageKind.SessionRejoinState, state.Plan.Id, StateHeaderSize + plan.Length);
        state.Attempt.TryWriteBytes(message.AsSpan(17));
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(33), state.PoseSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(37), state.SoundSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(41), state.LightSequence);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(45), state.ItemOperation);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(53), state.DrinkOperation);
        GameplayWire.WriteFloat(message.AsSpan(61), state.X);
        GameplayWire.WriteFloat(message.AsSpan(65), state.Y);
        message[69] = state.HasPosition ? (byte)1 : (byte)0;
        plan.CopyTo(message, StateHeaderSize);
        return message;
    }

    internal static bool TryReadState(ReadOnlySpan<byte> message, Guid session, out SessionRejoinState state)
    {
        state = default;
        if (message.Length <= StateHeaderSize || message[69] > 1 ||
            !GameplayWire.HasHeader(message, MessageKind.SessionRejoinState, session) ||
            RoomWire.ReadPlan(message.Slice(StateHeaderSize)) is not { } plan || plan.Id != session)
        {
            return false;
        }

        Guid attempt = new(message.Slice(17, 16));
        float x = GameplayWire.ReadFloat(message.Slice(61));
        float y = GameplayWire.ReadFloat(message.Slice(65));
        if (attempt == Guid.Empty || !GameplayWire.IsCoordinate(x) || !GameplayWire.IsCoordinate(y))
        {
            return false;
        }

        state = new SessionRejoinState(plan, attempt,
            BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(33)), BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(37)),
            BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(41)), BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(45)),
            BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(53)), x, y, message[69] != 0);
        return true;
    }
}
