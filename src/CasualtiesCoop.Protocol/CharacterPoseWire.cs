using System;
using System.Buffers.Binary;
using CasualtiesCoop.Gameplay.Items;
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
    internal readonly AnimatorLayerPose[] BodyLayers;
    internal readonly AnimatorLayerPose[] ArmLayers;
    internal readonly FacePose Face;
    internal readonly HeldItemPose[] HeldItems;

    internal CharacterPose(ulong player, uint sequence, float x, float y, float velocityX = 0, float velocityY = 0,
        bool facingRight = true, bool standing = true, LimbPose[]? limbs = null,
        AnimatorLayerPose[]? bodyLayers = null, AnimatorLayerPose[]? armLayers = null, FacePose face = default,
        HeldItemPose[]? heldItems = null)
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
        BodyLayers = bodyLayers ?? Array.Empty<AnimatorLayerPose>();
        ArmLayers = armLayers ?? Array.Empty<AnimatorLayerPose>();
        Face = face;
        HeldItems = heldItems ?? Array.Empty<HeldItemPose>();
    }
}

internal readonly struct HeldItemPose
{
    internal readonly ulong ItemId;
    internal readonly byte Slot;
    internal readonly float Angle;
    internal readonly bool Mirrored;

    internal HeldItemPose(ulong itemId, byte slot, float angle, bool mirrored)
    {
        ItemId = itemId;
        Slot = slot;
        Angle = angle;
        Mirrored = mirrored;
    }
}

internal readonly struct FacePose
{
    internal readonly byte Head;
    internal readonly byte Eyes;
    internal readonly byte NoseAlpha;
    internal readonly bool HasExpression;

    internal FacePose(byte head, byte eyes, byte noseAlpha, bool hasExpression = true)
    {
        Head = head;
        Eyes = eyes;
        NoseAlpha = noseAlpha;
        HasExpression = hasExpression;
    }

    internal bool ChangedExpression(FacePose previous) =>
        HasExpression != previous.HasExpression || Head != previous.Head || Eyes != previous.Eyes;
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

internal readonly struct AnimatorLayerPose
{
    internal readonly int State;
    internal readonly float Phase;
    internal readonly int NextState;
    internal readonly float NextPhase;
    internal readonly float Transition;

    internal AnimatorLayerPose(int state, float phase, int nextState = 0, float nextPhase = 0, float transition = 0)
    {
        State = state;
        Phase = phase;
        NextState = nextState;
        NextPhase = nextPhase;
        Transition = transition;
    }

    internal bool ChangedSince(AnimatorLayerPose previous) => State != previous.State || NextState != previous.NextState ||
        Phase < previous.Phase - 0.125f || (NextState != 0 && Transition < previous.Transition - 0.125f);
}

internal static class CharacterPoseWire
{
    internal const int MaxLimbs = 24;
    internal const int MaxLayers = 5;
    internal const int MaxHeldItems = 16;
    internal const float MaxPhase = 1_000_000f;
    private const int PositionSize = 58;
    private const int LimbSize = 20;
    private const int LayerSize = 20;
    private const int HeldItemSize = 14;

    internal static byte[] Write(Guid session, CharacterPose position)
    {
        int count = Math.Min(position.Limbs.Length, MaxLimbs);
        int bodyCount = Math.Min(position.BodyLayers.Length, MaxLayers);
        int armCount = Math.Min(position.ArmLayers.Length, MaxLayers);
        int heldCount = Math.Min(position.HeldItems.Length, MaxHeldItems);
        int layerOffset = PositionSize + count * LimbSize;
        int heldOffset = layerOffset + (bodyCount + armCount) * LayerSize;
        byte[] message = NewFrame(MessageKind.CharacterPose, session, heldOffset + heldCount * HeldItemSize);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), position.Player);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(25), position.Sequence);
        WriteFloat(message.AsSpan(29), position.X);
        WriteFloat(message.AsSpan(33), position.Y);
        WriteFloat(message.AsSpan(37), position.VelocityX);
        WriteFloat(message.AsSpan(41), position.VelocityY);
        message[45] = (byte)((position.FacingRight ? 1 : 0) | (position.Standing ? 2 : 0));
        message[46] = (byte)count;
        message[51] = (byte)bodyCount;
        message[52] = (byte)armCount;
        message[53] = position.Face.HasExpression ? (byte)1 : (byte)0;
        message[54] = position.Face.Head;
        message[55] = position.Face.Eyes;
        message[56] = position.Face.NoseAlpha;
        message[57] = (byte)heldCount;
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
        WriteLayers(message.AsSpan(layerOffset), position.BodyLayers.AsSpan(0, bodyCount));
        WriteLayers(message.AsSpan(layerOffset + bodyCount * LayerSize), position.ArmLayers.AsSpan(0, armCount));
        WriteHeldItems(message.AsSpan(heldOffset), position.HeldItems.AsSpan(0, heldCount));
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
        int bodyCount = message[51];
        int armCount = message[52];
        int heldCount = message[57];
        int layerOffset = PositionSize + count * LimbSize;
        int heldOffset = layerOffset + (bodyCount + armCount) * LayerSize;
        if (player == 0 || !IsCoordinate(x) || !IsCoordinate(y) || !InRange(vx, 1000) || !InRange(vy, 1000) ||
            message[45] > 3 || message[53] > 1 || count > MaxLimbs || bodyCount > MaxLayers || armCount > MaxLayers ||
            heldCount > MaxHeldItems || message.Length != heldOffset + heldCount * HeldItemSize)
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

        if (!TryReadLayers(message.Slice(layerOffset), bodyCount, out AnimatorLayerPose[] bodyLayers) ||
            !TryReadLayers(message.Slice(layerOffset + bodyCount * LayerSize), armCount, out AnimatorLayerPose[] armLayers) ||
            !TryReadHeldItems(message.Slice(heldOffset), heldCount, out HeldItemPose[] heldItems))
        {
            return false;
        }

        position = new CharacterPose(player, sequence, x, y, vx, vy, (message[45] & 1) != 0, (message[45] & 2) != 0,
            limbs, bodyLayers, armLayers, new FacePose(message[54], message[55], message[56], message[53] != 0), heldItems);
        return true;
    }

    private static void WriteHeldItems(Span<byte> bytes, ReadOnlySpan<HeldItemPose> heldItems)
    {
        foreach (HeldItemPose held in heldItems)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, held.ItemId);
            bytes[8] = held.Slot;
            WriteFloat(bytes.Slice(9), held.Angle);
            bytes[13] = held.Mirrored ? (byte)1 : (byte)0;
            bytes = bytes.Slice(HeldItemSize);
        }
    }

    private static bool TryReadHeldItems(ReadOnlySpan<byte> bytes, int count, out HeldItemPose[] heldItems)
    {
        heldItems = count == 0 ? Array.Empty<HeldItemPose>() : new HeldItemPose[count];
        for (int index = 0; index < heldItems.Length; index++)
        {
            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            byte slot = bytes[8];
            float angle = ReadFloat(bytes.Slice(9));
            byte mirrored = bytes[13];
            if (id == 0 || slot == ItemWire.NoSlot || angle < 0f || !InRange(angle, 360f) || mirrored > 1)
            {
                return false;
            }

            for (int previous = 0; previous < index; previous++)
            {
                if (heldItems[previous].ItemId == id || heldItems[previous].Slot == slot)
                {
                    return false;
                }
            }

            heldItems[index] = new HeldItemPose(id, slot, angle, mirrored == 1);
            bytes = bytes.Slice(HeldItemSize);
        }
        return true;
    }

    private static void WriteLayers(Span<byte> bytes, ReadOnlySpan<AnimatorLayerPose> layers)
    {
        foreach (AnimatorLayerPose layer in layers)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes, layer.State);
            WriteFloat(bytes.Slice(4), layer.Phase);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(8), layer.NextState);
            WriteFloat(bytes.Slice(12), layer.NextPhase);
            WriteFloat(bytes.Slice(16), layer.Transition);
            bytes = bytes.Slice(LayerSize);
        }
    }

    private static bool TryReadLayers(ReadOnlySpan<byte> bytes, int count, out AnimatorLayerPose[] layers)
    {
        layers = count == 0 ? Array.Empty<AnimatorLayerPose>() : new AnimatorLayerPose[count];
        for (int index = 0; index < layers.Length; index++)
        {
            float phase = ReadFloat(bytes.Slice(4));
            float nextPhase = ReadFloat(bytes.Slice(12));
            float transition = ReadFloat(bytes.Slice(16));
            if (!InRange(phase, MaxPhase) || !InRange(nextPhase, MaxPhase) || !InRange(transition, 1f) || transition < 0f)
            {
                return false;
            }

            layers[index] = new AnimatorLayerPose(BinaryPrimitives.ReadInt32LittleEndian(bytes), phase,
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8)), nextPhase, transition);
            bytes = bytes.Slice(LayerSize);
        }
        return true;
    }
}
