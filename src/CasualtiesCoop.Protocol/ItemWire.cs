using CasualtiesCoop.Networking;
using System;
using System.Buffers.Binary;
using System.Text;

namespace CasualtiesCoop.Gameplay.Items;

internal enum ItemAction : byte
{
    PickUp = 1,
    Drop,
    Throw,
    SwapSlots
}

internal enum ItemDecision : byte
{
    Accepted = 1,
    Unavailable,
    OutOfReach,
    SlotOccupied,
    Conflict
}

internal enum ItemPlacement : byte
{
    World = 0,
    Inventory = 1,
    Container = 2,
    Worn = 3
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
    internal float SampleTime;
    internal string Definition;
    internal ulong Owner;
    internal ulong Parent;
    internal byte Slot;
    internal ulong ClientKey;
    internal ItemPlacement Placement;
    internal bool Removed;
    internal float Condition;
    internal ItemLocation Location;
    internal byte[] Payload;
}

internal struct SharedItemRequest
{
    internal ulong Operation;
    internal ulong ItemId;
    internal uint Revision;
    internal ItemAction Action;
    internal byte Slot;
    internal byte OtherSlot;
    internal ulong OtherItemId;
    internal uint OtherRevision;
    internal ItemLocation Location;
    internal byte[] Payload;
}

internal struct SharedItemRegistration
{
    internal ulong Operation;
    internal ulong ClientKey;
    internal string Definition;
    internal ItemPlacement Placement;
    internal ulong Parent;
    internal byte Slot;
    internal ItemLocation Location;
    internal float Condition;
    internal byte[] Payload;
}

internal struct SharedItemStateReport
{
    internal ulong Operation;
    internal ulong ItemId;
    internal uint Revision;
    internal float Condition;
    internal ItemPlacement Placement;
    internal ulong Parent;
    internal byte Slot;
    internal ItemLocation Location;
    internal byte[] Payload;
}

internal static class ItemWire
{
    internal const int MaxDefinitionLength = 64;
    internal const int MaxPayloadLength = 12288;
    internal const byte NoSlot = byte.MaxValue;

    private const int StateHeaderSize = 87;
    private const int RequestHeaderSize = 65;
    private const int SwapRequestSize = RequestHeaderSize + 1 + sizeof(ulong) + sizeof(uint);
    private const int RegistrationHeaderSize = 74;
    private const int RegisteredSize = 45;
    private const int ReportHeaderSize = 77;
    private const int ResultSize = 26;

    internal static byte[] WriteState(MessageKind frame, Guid session, SharedItemState state)
    {
        byte[] definition = Encoding.ASCII.GetBytes(state.Definition);
        byte[] payload = frame == MessageKind.ItemMotion || state.Removed ? Array.Empty<byte>() : state.Payload ?? Array.Empty<byte>();
        byte[] message = GameplayWire.NewFrame(frame, session, StateHeaderSize + definition.Length + payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), state.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(25), state.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(29), state.Sample);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(33), state.Owner);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(41), state.Parent);
        message[49] = state.Slot;
        message[50] = (byte)state.Placement;
        message[51] = (byte)definition.Length;
        message[52] = state.Removed ? (byte)1 : (byte)0;
        GameplayWire.WriteFloat(message.AsSpan(53), state.Condition);
        WriteLocation(message.AsSpan(57), state.Location);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(81), (ushort)payload.Length);
        GameplayWire.WriteFloat(message.AsSpan(83), state.SampleTime);
        definition.CopyTo(message, StateHeaderSize);
        payload.CopyTo(message, StateHeaderSize + definition.Length);
        return message;
    }

    internal static bool TryReadState(ReadOnlySpan<byte> message, MessageKind frame, Guid session, out SharedItemState state)
    {
        state = default;
        if (!GameplayWire.HasHeader(message, frame, session) || message.Length < StateHeaderSize)
        {
            return false;
        }

        int definitionLength = message[51];
        int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(message.Slice(81));
        if (definitionLength is < 1 or > MaxDefinitionLength || payloadLength > MaxPayloadLength ||
            message.Length != StateHeaderSize + definitionLength + payloadLength ||
            (frame == MessageKind.ItemMotion && payloadLength != 0))
        {
            return false;
        }

        state.Id = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(17));
        state.Revision = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(25));
        state.Sample = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(29));
        state.SampleTime = GameplayWire.ReadFloat(message.Slice(83));
        state.Owner = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(33));
        state.Parent = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(41));
        state.Slot = message[49];
        state.Placement = (ItemPlacement)message[50];
        state.Definition = Encoding.ASCII.GetString(message.Slice(StateHeaderSize, definitionLength));
        state.Removed = message[52] == 1;
        state.Condition = GameplayWire.ReadFloat(message.Slice(53));
        state.Location = ReadLocation(message.Slice(57));
        state.Payload = message.Slice(StateHeaderSize + definitionLength, payloadLength).ToArray();
        return state.Id != 0 && state.Revision != 0 && IsDefinition(state.Definition) && message[52] <= 1 &&
            state.Placement <= ItemPlacement.Worn && IsPlacementValid(state) && state.Condition is >= 0f and <= 1f &&
            float.IsFinite(state.SampleTime) && state.SampleTime >= 0f && IsLocationValid(state.Location);
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

    internal static bool IsPayload(byte[]? payload) => payload is not null && payload.Length <= MaxPayloadLength;

    internal static byte[] WriteRequest(Guid session, SharedItemRequest request)
    {
        byte[] payload = request.Payload ?? Array.Empty<byte>();
        bool swap = request.Action == ItemAction.SwapSlots;
        byte[] message = GameplayWire.NewFrame(MessageKind.ItemRequest, session,
            swap ? SwapRequestSize : RequestHeaderSize + payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), request.Operation);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(25), request.ItemId);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(33), request.Revision);
        message[37] = (byte)request.Action;
        message[38] = request.Slot;
        if (swap)
        {
            message[RequestHeaderSize] = request.OtherSlot;
            BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(RequestHeaderSize + 1), request.OtherItemId);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(RequestHeaderSize + 1 + sizeof(ulong)), request.OtherRevision);
            return message;
        }
        WriteLocation(message.AsSpan(39), request.Location);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(63), (ushort)payload.Length);
        payload.CopyTo(message, RequestHeaderSize);
        return message;
    }

    internal static bool TryReadRequest(ReadOnlySpan<byte> message, Guid session, out SharedItemRequest request)
    {
        request = default;
        if (!GameplayWire.HasHeader(message, MessageKind.ItemRequest, session) || message.Length < RequestHeaderSize)
        {
            return false;
        }

        request.Action = (ItemAction)message[37];
        bool swap = request.Action == ItemAction.SwapSlots;
        int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(message.Slice(63));
        if (swap ? payloadLength != 0 || message.Length != SwapRequestSize
                 : payloadLength > MaxPayloadLength || message.Length != RequestHeaderSize + payloadLength)
        {
            return false;
        }

        request.Operation = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(17));
        request.ItemId = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(25));
        request.Revision = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(33));
        request.Slot = message[38];
        request.Location = ReadLocation(message.Slice(39));
        if (request.Operation == 0 || request.ItemId == 0 || request.Revision == 0 || request.Slot == NoSlot ||
            !IsLocationValid(request.Location))
        {
            return false;
        }
        if (swap)
        {
            request.OtherSlot = message[RequestHeaderSize];
            request.OtherItemId = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(RequestHeaderSize + 1));
            request.OtherRevision = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(RequestHeaderSize + 1 + sizeof(ulong)));
            request.Payload = Array.Empty<byte>();
            return IsValidSwap(request);
        }
        request.Payload = message.Slice(RequestHeaderSize).ToArray();
        return request.Action is >= ItemAction.PickUp and <= ItemAction.Throw;
    }

    internal static bool IsValidSwap(SharedItemRequest request) =>
        request.Slot != NoSlot && request.OtherSlot != NoSlot && request.Slot != request.OtherSlot &&
        request.OtherItemId != request.ItemId && (request.OtherItemId == 0) == (request.OtherRevision == 0);

    internal static byte[] WriteRegistration(Guid session, SharedItemRegistration registration)
    {
        byte[] definition = Encoding.ASCII.GetBytes(registration.Definition);
        byte[] payload = registration.Payload ?? Array.Empty<byte>();
        byte[] message = GameplayWire.NewFrame(MessageKind.ItemRegister, session,
            RegistrationHeaderSize + definition.Length + payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), registration.Operation);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(25), registration.ClientKey);
        message[33] = (byte)definition.Length;
        message[34] = (byte)registration.Placement;
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(35), registration.Parent);
        message[43] = registration.Slot;
        GameplayWire.WriteFloat(message.AsSpan(44), registration.Condition);
        WriteLocation(message.AsSpan(48), registration.Location);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(72), (ushort)payload.Length);
        definition.CopyTo(message, RegistrationHeaderSize);
        payload.CopyTo(message, RegistrationHeaderSize + definition.Length);
        return message;
    }

    internal static bool TryReadRegistration(ReadOnlySpan<byte> message, Guid session, out SharedItemRegistration registration)
    {
        registration = default;
        if (!GameplayWire.HasHeader(message, MessageKind.ItemRegister, session) || message.Length < RegistrationHeaderSize)
        {
            return false;
        }

        int definitionLength = message[33];
        int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(message.Slice(72));
        if (definitionLength is < 1 or > MaxDefinitionLength || payloadLength > MaxPayloadLength ||
            message.Length != RegistrationHeaderSize + definitionLength + payloadLength)
        {
            return false;
        }

        registration.Operation = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(17));
        registration.ClientKey = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(25));
        registration.Definition = Encoding.ASCII.GetString(message.Slice(RegistrationHeaderSize, definitionLength));
        registration.Placement = (ItemPlacement)message[34];
        registration.Parent = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(35));
        registration.Slot = message[43];
        registration.Condition = GameplayWire.ReadFloat(message.Slice(44));
        registration.Location = ReadLocation(message.Slice(48));
        registration.Payload = message.Slice(RegistrationHeaderSize + definitionLength, payloadLength).ToArray();
        var state = new SharedItemState
        {
            Owner = registration.Placement == ItemPlacement.Inventory || registration.Placement == ItemPlacement.Worn
                ? ulong.MaxValue : 0,
            Parent = registration.Parent, Slot = registration.Slot, Placement = registration.Placement
        };
        return registration.Operation != 0 && registration.ClientKey != 0 && IsDefinition(registration.Definition) &&
            registration.Placement is >= ItemPlacement.World and <= ItemPlacement.Worn && IsPlacementValid(state) &&
            IsLocationValid(registration.Location) && registration.Condition is >= 0f and <= 1f;
    }

    internal static byte[] WriteRegistered(Guid session, ulong operation, ulong clientKey, ulong item, uint revision)
    {
        byte[] message = GameplayWire.NewFrame(MessageKind.ItemRegistered, session, RegisteredSize);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), operation);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(25), clientKey);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(33), item);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(41), revision);
        return message;
    }

    internal static bool TryReadRegistered(ReadOnlySpan<byte> message, Guid session, out ulong operation,
        out ulong clientKey, out ulong item, out uint revision)
    {
        operation = clientKey = item = revision = 0;
        if (message.Length != RegisteredSize || !GameplayWire.HasHeader(message, MessageKind.ItemRegistered, session))
        {
            return false;
        }
        operation = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(17));
        clientKey = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(25));
        item = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(33));
        revision = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(41));
        return operation != 0 && clientKey != 0 && item != 0 && revision != 0;
    }

    internal static byte[] WriteReport(Guid session, SharedItemStateReport report)
    {
        byte[] payload = report.Payload ?? Array.Empty<byte>();
        byte[] message = GameplayWire.NewFrame(MessageKind.ItemStateReport, session, ReportHeaderSize + payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), report.Operation);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(25), report.ItemId);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(33), report.Revision);
        GameplayWire.WriteFloat(message.AsSpan(37), report.Condition);
        message[41] = (byte)report.Placement;
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(42), report.Parent);
        message[50] = report.Slot;
        WriteLocation(message.AsSpan(51), report.Location);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(75), (ushort)payload.Length);
        payload.CopyTo(message, ReportHeaderSize);
        return message;
    }

    internal static bool TryReadReport(ReadOnlySpan<byte> message, Guid session, out SharedItemStateReport report)
    {
        report = default;
        if (!GameplayWire.HasHeader(message, MessageKind.ItemStateReport, session) || message.Length < ReportHeaderSize)
        {
            return false;
        }
        int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(message.Slice(75));
        if (payloadLength > MaxPayloadLength || message.Length != ReportHeaderSize + payloadLength)
        {
            return false;
        }
        report.Operation = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(17));
        report.ItemId = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(25));
        report.Revision = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(33));
        report.Condition = GameplayWire.ReadFloat(message.Slice(37));
        report.Placement = (ItemPlacement)message[41];
        report.Parent = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(42));
        report.Slot = message[50];
        report.Location = ReadLocation(message.Slice(51));
        report.Payload = message.Slice(ReportHeaderSize).ToArray();
        var structure = new SharedItemState
        {
            Placement = report.Placement, Parent = report.Parent, Slot = report.Slot,
            Owner = report.Placement is ItemPlacement.Inventory or ItemPlacement.Worn ? ulong.MaxValue : 0
        };
        return report.Operation != 0 && report.ItemId != 0 && report.Revision != 0 &&
            report.Condition is >= 0f and <= 1f && IsPlacementValid(structure) &&
            IsLocationValid(report.Location);
    }

    internal static byte[] WriteResult(Guid session, ulong operation, ItemDecision decision)
    {
        byte[] message = GameplayWire.NewFrame(MessageKind.ItemResult, session, ResultSize);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), operation);
        message[25] = (byte)decision;
        return message;
    }

    internal static bool TryReadResult(ReadOnlySpan<byte> message, Guid session, out ulong operation, out ItemDecision decision)
    {
        operation = 0;
        decision = default;
        if (message.Length != ResultSize || !GameplayWire.HasHeader(message, MessageKind.ItemResult, session))
        {
            return false;
        }

        operation = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(17));
        decision = (ItemDecision)message[25];
        return operation != 0 && decision is >= ItemDecision.Accepted and <= ItemDecision.Conflict;
    }

    private static bool IsPlacementValid(SharedItemState state)
    {
        if (state.Removed)
        {
            return state.Owner == 0 && state.Parent == 0 && state.Slot == NoSlot;
        }
        return state.Placement switch
        {
            ItemPlacement.World => state.Owner == 0 && state.Parent == 0 && state.Slot == NoSlot,
            ItemPlacement.Inventory => state.Owner != 0 && state.Parent == 0 && state.Slot != NoSlot,
            ItemPlacement.Worn => state.Owner != 0 && state.Parent == 0 && state.Slot == NoSlot,
            ItemPlacement.Container => state.Owner == 0 && state.Parent != 0 && state.Slot == NoSlot,
            _ => false
        };
    }

    internal static bool IsLocationValid(ItemLocation location) =>
        GameplayWire.IsCoordinate(location.X) && GameplayWire.IsCoordinate(location.Y) &&
        GameplayWire.InRange(location.Angle, 720) && GameplayWire.InRange(location.VelocityX, 1000) &&
        GameplayWire.InRange(location.VelocityY, 1000) && GameplayWire.InRange(location.Spin, 20000);

    private static void WriteLocation(Span<byte> message, ItemLocation location)
    {
        GameplayWire.WriteFloat(message, location.X);
        GameplayWire.WriteFloat(message.Slice(4), location.Y);
        GameplayWire.WriteFloat(message.Slice(8), location.Angle);
        GameplayWire.WriteFloat(message.Slice(12), location.VelocityX);
        GameplayWire.WriteFloat(message.Slice(16), location.VelocityY);
        GameplayWire.WriteFloat(message.Slice(20), location.Spin);
    }

    private static ItemLocation ReadLocation(ReadOnlySpan<byte> message) =>
        new(GameplayWire.ReadFloat(message), GameplayWire.ReadFloat(message.Slice(4)), GameplayWire.ReadFloat(message.Slice(8)),
            GameplayWire.ReadFloat(message.Slice(12)), GameplayWire.ReadFloat(message.Slice(16)), GameplayWire.ReadFloat(message.Slice(20)));
}
