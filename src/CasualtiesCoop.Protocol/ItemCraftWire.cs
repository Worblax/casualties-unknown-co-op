using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CasualtiesCoop.Networking;

namespace CasualtiesCoop.Gameplay.Items;

internal struct ItemCraftRequest
{
    internal ulong Operation;
    internal int Recipe;
    internal ulong[] ItemIds;
    internal uint[] Revisions;
}

internal sealed class ItemCraftCommit
{
    internal ulong Operation;
    internal int Recipe;
    internal List<ulong> Removed = new();
    internal List<CraftUpdatedItem> Updated = new();
    internal List<CraftCreatedItem> Created = new();
}

internal struct CraftUpdatedItem
{
    internal ulong Id;
    internal uint Revision;
    internal float Condition;
    internal byte[] Payload;
}

internal struct CraftCreatedItem
{
    internal ulong ClientKey;
    internal string Definition;
    internal ItemPlacement Placement;
    internal ulong Parent;
    internal byte Slot;
    internal ItemLocation Location;
    internal byte[] Payload;
}

internal static class ItemCraftWire
{
    internal const int MaxIngredients = 64;
    internal const int MaxCommitItems = 64;
    private const int CommitHeader = 30;
    private const int RequestHeader = 30;
    private const int ResultSize = 30;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static byte[] WriteRequest(Guid session, ItemCraftRequest request)
    {
        int count = request.ItemIds?.Length ?? 0;
        if (count is < 1 or > MaxIngredients || request.Revisions?.Length != count)
        {
            throw new ArgumentException("Invalid craft ingredient list.");
        }
        byte[] frame = GameplayWire.NewFrame(MessageKind.ItemCraftRequest, session, RequestHeader + count * 12);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(17), request.Operation);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(25), request.Recipe);
        frame[29] = (byte)count;
        int offset = RequestHeader;
        for (int index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(offset), request.ItemIds![index]);
            offset += 8;
        }
        for (int index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), request.Revisions[index]);
            offset += 4;
        }
        return frame;
    }

    internal static bool TryReadRequest(ReadOnlySpan<byte> frame, Guid session, out ItemCraftRequest request)
    {
        request = default;
        if (!GameplayWire.HasHeader(frame, MessageKind.ItemCraftRequest, session) || frame.Length < RequestHeader)
        {
            return false;
        }
        int count = frame[29];
        if (count is < 1 or > MaxIngredients || frame.Length != RequestHeader + count * 12)
        {
            return false;
        }
        var ids = new ulong[count];
        var revisions = new uint[count];
        request.Operation = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(17));
        request.Recipe = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(25));
        int offset = RequestHeader;
        for (int index = 0; index < count; index++)
        {
            ids[index] = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(offset));
            offset += 8;
        }
        for (int index = 0; index < count; index++)
        {
            revisions[index] = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset));
            offset += 4;
        }
        request.ItemIds = ids;
        request.Revisions = revisions;
        return request.Operation != 0 && request.Recipe >= 0 && ids.All(id => id != 0) && revisions.All(value => value != 0) &&
            ids.Distinct().Count() == ids.Length;
    }

    internal static byte[] WriteResult(Guid session, ulong operation, int recipe, ItemDecision decision)
    {
        byte[] frame = GameplayWire.NewFrame(MessageKind.ItemCraftResult, session, ResultSize);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(17), operation);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(25), recipe);
        frame[29] = (byte)decision;
        return frame;
    }

    internal static bool TryReadResult(ReadOnlySpan<byte> frame, Guid session, out ulong operation, out int recipe,
        out ItemDecision decision)
    {
        operation = 0;
        recipe = -1;
        decision = default;
        if (frame.Length != ResultSize || !GameplayWire.HasHeader(frame, MessageKind.ItemCraftResult, session))
        {
            return false;
        }
        operation = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(17));
        recipe = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(25));
        decision = (ItemDecision)frame[29];
        return operation != 0 && recipe >= 0 && decision is >= ItemDecision.Accepted and <= ItemDecision.Conflict;
    }

    internal static byte[] WriteCommit(Guid session, ItemCraftCommit commit)
    {
        if (commit.Removed.Count > MaxCommitItems || commit.Updated.Count > MaxCommitItems ||
            commit.Created.Count > MaxCommitItems)
        {
            throw new ArgumentException("Craft commit exceeds its bounds.");
        }
        using var stream = new System.IO.MemoryStream(256);
        using var writer = new System.IO.BinaryWriter(stream, Utf8, leaveOpen: true);
        byte[] header = GameplayWire.NewFrame(MessageKind.ItemCraftCommit, session, CommitHeader);
        writer.Write(header);
        writer.Write(commit.Operation);
        writer.Write(commit.Recipe);
        writer.Write((byte)commit.Removed.Count);
        writer.Write((byte)commit.Updated.Count);
        writer.Write((byte)commit.Created.Count);
        foreach (ulong id in commit.Removed) writer.Write(id);
        foreach (CraftUpdatedItem item in commit.Updated)
        {
            writer.Write(item.Id);
            writer.Write(item.Revision);
            writer.Write(item.Condition);
            WriteBytes(writer, item.Payload);
        }
        foreach (CraftCreatedItem item in commit.Created)
        {
            writer.Write(item.ClientKey);
            writer.Write((byte)Utf8.GetByteCount(item.Definition));
            writer.Write((byte)item.Placement);
            writer.Write(item.Parent);
            writer.Write(item.Slot);
            WriteLocation(writer, item.Location);
            WriteBytes(writer, item.Payload);
            WriteString(writer, item.Definition);
        }
        writer.Flush();
        return stream.ToArray();
    }

    internal static bool TryReadCommit(ReadOnlySpan<byte> frame, Guid session, out ItemCraftCommit commit)
    {
        commit = new ItemCraftCommit();
        if (!GameplayWire.HasHeader(frame, MessageKind.ItemCraftCommit, session) || frame.Length < CommitHeader)
        {
            return false;
        }
        using var stream = new System.IO.MemoryStream(frame.ToArray());
        using var reader = new System.IO.BinaryReader(stream, Utf8, leaveOpen: false);
        reader.ReadBytes(CommitHeader);
        commit.Operation = reader.ReadUInt64();
        commit.Recipe = reader.ReadInt32();
        int removed = reader.ReadByte();
        int updated = reader.ReadByte();
        int created = reader.ReadByte();
        if (removed > MaxCommitItems || updated > MaxCommitItems || created > MaxCommitItems)
        {
            return false;
        }
        for (int index = 0; index < removed; index++) commit.Removed.Add(reader.ReadUInt64());
        for (int index = 0; index < updated; index++)
        {
            commit.Updated.Add(new CraftUpdatedItem
            {
                Id = reader.ReadUInt64(), Revision = reader.ReadUInt32(), Condition = reader.ReadSingle(),
                Payload = ReadBytes(reader)
            });
        }
        for (int index = 0; index < created; index++)
        {
            ulong clientKey = reader.ReadUInt64();
            int definitionLength = reader.ReadByte();
            var placement = (ItemPlacement)reader.ReadByte();
            ulong parent = reader.ReadUInt64();
            byte slot = reader.ReadByte();
            ItemLocation location = ReadLocation(reader);
            byte[] payload = ReadBytes(reader);
            string definition = ReadString(reader, definitionLength);
            if (clientKey == 0 || !ItemWire.IsDefinition(definition) || !ItemWire.IsPayload(payload) ||
                placement > ItemPlacement.Worn)
            {
                return false;
            }
            commit.Created.Add(new CraftCreatedItem
            {
                ClientKey = clientKey, Definition = definition, Placement = placement, Parent = parent, Slot = slot,
                Location = location, Payload = payload
            });
        }
        return stream.Position == stream.Length && commit.Operation != 0 && commit.Recipe >= 0 &&
            (commit.Removed.Count > 0 || commit.Updated.Count > 0 || commit.Created.Count > 0);
    }

    private static void WriteBytes(System.IO.BinaryWriter writer, byte[]? data)
    {
        data ??= Array.Empty<byte>();
        writer.Write((ushort)data.Length);
        writer.Write(data);
    }

    private static byte[] ReadBytes(System.IO.BinaryReader reader)
    {
        ushort length = reader.ReadUInt16();
        if (length > ItemWire.MaxPayloadLength) throw new System.IO.InvalidDataException("Craft payload too large.");
        return reader.ReadBytes(length);
    }

    private static void WriteString(System.IO.BinaryWriter writer, string value) =>
        writer.Write(Utf8.GetBytes(value));

    private static string ReadString(System.IO.BinaryReader reader, int length)
    {
        if (length is < 1 or > ItemWire.MaxDefinitionLength) return "";
        return Utf8.GetString(reader.ReadBytes(length));
    }

    private static void WriteLocation(System.IO.BinaryWriter writer, ItemLocation location)
    {
        writer.Write(location.X);
        writer.Write(location.Y);
        writer.Write(location.Angle);
        writer.Write(location.VelocityX);
        writer.Write(location.VelocityY);
        writer.Write(location.Spin);
    }

    private static ItemLocation ReadLocation(System.IO.BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}