using System;
using System.Buffers.Binary;
using System.Text;

namespace CasualtiesCoop.Networking;

internal enum ItemAction : byte
{
    PickUp = 1,
    Drop,
    Throw
}

internal enum ItemDecision : byte
{
    Accepted = 1,
    Unavailable,
    OutOfReach,
    SlotOccupied
}

internal readonly struct ItemLocation
{
    internal readonly float X;
    internal readonly float Y;
    internal readonly float Angle;
    internal readonly float VelocityX;
    internal readonly float VelocityY;
    internal readonly float Spin;

    internal ItemLocation(float x, float y, float angle, float velocityX = 0, float velocityY = 0, float spin = 0)
    {
        X = x;
        Y = y;
        Angle = angle;
        VelocityX = velocityX;
        VelocityY = velocityY;
        Spin = spin;
    }
}

internal struct SharedItemState
{
    internal ulong Id;
    internal uint Revision;
    internal uint Sample;
    internal string Definition;
    internal ulong Owner;
    internal byte Slot;
    internal bool Removed;
    internal float Condition;
    internal ItemLocation Location;
}

internal struct SharedItemRequest
{
    internal ulong Operation;
    internal ulong ItemId;
    internal uint Revision;
    internal ItemAction Action;
    internal byte Slot;
    internal ItemLocation Location;
}

internal static class ItemWire
{
    private const int StateHeaderSize = 72;
    private const int MaxDefinitionLength = 64;
    private const int RequestSize = 59;
    private const int ResultSize = 26;
    internal const byte NoSlot = byte.MaxValue;

    internal static byte[] WriteState(RoomFrame frame, Guid session, SharedItemState state)
    {
        byte[] definition = Encoding.ASCII.GetBytes(state.Definition);
        byte[] message = SandboxWire.NewFrame(frame, session, StateHeaderSize + definition.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), state.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(25), state.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(29), state.Sample);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(33), state.Owner);
        message[41] = state.Slot;
        message[42] = (byte)definition.Length;
        message[43] = state.Removed ? (byte)1 : (byte)0;
        SandboxWire.WriteFloat(message.AsSpan(44), state.Condition);
        WriteLocation(message.AsSpan(48), state.Location);
        definition.CopyTo(message, StateHeaderSize);
        return message;
    }

    internal static bool TryReadState(ReadOnlySpan<byte> message, RoomFrame frame, Guid session, out SharedItemState state)
    {
        state = default;
        int length = message.Length - StateHeaderSize;
        if (length is < 1 or > MaxDefinitionLength || !SandboxWire.HasHeader(message, frame, session) || message[42] != length)
        {
            return false;
        }

        state.Id = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(17));
        state.Revision = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(25));
        state.Sample = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(29));
        state.Owner = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(33));
        state.Slot = message[41];
        state.Definition = Encoding.ASCII.GetString(message.Slice(StateHeaderSize));
        state.Removed = message[43] == 1;
        state.Condition = SandboxWire.ReadFloat(message.Slice(44));
        state.Location = ReadLocation(message.Slice(48));
        return state.Id != 0 && state.Revision != 0 && IsDefinition(state.Definition) &&
            message[43] <= 1 && ((state.Owner == 0) == (state.Slot == NoSlot)) && (!state.Removed || state.Owner == 0) &&
            state.Condition is >= 0f and <= 1f && IsLocationValid(state.Location);
    }

    internal static bool IsDefinition(string? definition)
    {
        if (definition is null || definition.Length is < 1 or > MaxDefinitionLength)
        {
            return false;
        }

        foreach (char c in definition)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
            {
                return false;
            }
        }
        return true;
    }

    internal static byte[] WriteRequest(Guid session, SharedItemRequest request)
    {
        byte[] message = SandboxWire.NewFrame(RoomFrame.ItemRequest, session, RequestSize);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), request.Operation);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(25), request.ItemId);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(33), request.Revision);
        message[37] = (byte)request.Action;
        message[38] = request.Slot;
        ItemLocation location = request.Location;
        SandboxWire.WriteFloat(message.AsSpan(39), location.X);
        SandboxWire.WriteFloat(message.AsSpan(43), location.Y);
        SandboxWire.WriteFloat(message.AsSpan(47), location.Angle);
        SandboxWire.WriteFloat(message.AsSpan(51), location.VelocityX);
        SandboxWire.WriteFloat(message.AsSpan(55), location.VelocityY);
        return message;
    }

    internal static bool TryReadRequest(ReadOnlySpan<byte> message, Guid session, out SharedItemRequest request)
    {
        request = default;
        if (message.Length != RequestSize || !SandboxWire.HasHeader(message, RoomFrame.ItemRequest, session))
        {
            return false;
        }

        request.Operation = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(17));
        request.ItemId = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(25));
        request.Revision = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(33));
        request.Action = (ItemAction)message[37];
        request.Slot = message[38];
        request.Location = new ItemLocation(SandboxWire.ReadFloat(message.Slice(39)), SandboxWire.ReadFloat(message.Slice(43)),
            SandboxWire.ReadFloat(message.Slice(47)), SandboxWire.ReadFloat(message.Slice(51)), SandboxWire.ReadFloat(message.Slice(55)));
        return request.Operation != 0 && request.ItemId != 0 && request.Revision != 0 && request.Slot != NoSlot &&
            request.Action is >= ItemAction.PickUp and <= ItemAction.Throw && IsLocationValid(request.Location);
    }

    internal static byte[] WriteResult(Guid session, ulong operation, ItemDecision decision)
    {
        byte[] message = SandboxWire.NewFrame(RoomFrame.ItemResult, session, ResultSize);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), operation);
        message[25] = (byte)decision;
        return message;
    }

    internal static bool TryReadResult(ReadOnlySpan<byte> message, Guid session, out ulong operation, out ItemDecision decision)
    {
        operation = 0;
        decision = default;
        if (message.Length != ResultSize || !SandboxWire.HasHeader(message, RoomFrame.ItemResult, session))
        {
            return false;
        }

        operation = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(17));
        decision = (ItemDecision)message[25];
        return operation != 0 && decision is >= ItemDecision.Accepted and <= ItemDecision.SlotOccupied;
    }

    private static bool IsLocationValid(ItemLocation location) =>
        SandboxWire.IsCoordinate(location.X) && SandboxWire.IsCoordinate(location.Y) &&
        SandboxWire.InRange(location.Angle, 720) && SandboxWire.InRange(location.VelocityX, 1000) &&
        SandboxWire.InRange(location.VelocityY, 1000) && SandboxWire.InRange(location.Spin, 20000);

    private static void WriteLocation(Span<byte> message, ItemLocation location)
    {
        SandboxWire.WriteFloat(message, location.X);
        SandboxWire.WriteFloat(message.Slice(4), location.Y);
        SandboxWire.WriteFloat(message.Slice(8), location.Angle);
        SandboxWire.WriteFloat(message.Slice(12), location.VelocityX);
        SandboxWire.WriteFloat(message.Slice(16), location.VelocityY);
        SandboxWire.WriteFloat(message.Slice(20), location.Spin);
    }

    private static ItemLocation ReadLocation(ReadOnlySpan<byte> message) =>
        new(SandboxWire.ReadFloat(message), SandboxWire.ReadFloat(message.Slice(4)), SandboxWire.ReadFloat(message.Slice(8)),
            SandboxWire.ReadFloat(message.Slice(12)), SandboxWire.ReadFloat(message.Slice(16)), SandboxWire.ReadFloat(message.Slice(20)));
}
