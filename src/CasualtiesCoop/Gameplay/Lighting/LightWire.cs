using CasualtiesCoop.Networking;
using System;
using System.Buffers.Binary;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Lighting;

internal struct LightSample
{
    internal uint Id;
    internal byte Definition;
    internal byte Component;
    internal bool World;
    internal bool Blink;
    internal byte BlinkPhase;
    internal Vector2 Position;
    internal float Angle;
    internal float Intensity;
    internal Vector2 Scale;
    internal Color32 Color;
}

internal static class LightWire
{
    internal const int MaxSources = 16;
    private const int HeaderSize = 30;
    private const int SampleSize = 36;

    internal static byte[] Write(Guid session, ulong player, uint sequence, ReadOnlySpan<LightSample> samples)
    {
        byte[] message = GameplayWire.NewFrame(MessageKind.LightSnapshot, session, HeaderSize + samples.Length * SampleSize);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(17), player);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(25), sequence);
        message[29] = (byte)samples.Length;
        for (int index = 0; index < samples.Length; index++)
        {
            LightSample sample = samples[index];
            Span<byte> data = message.AsSpan(HeaderSize + index * SampleSize);
            BinaryPrimitives.WriteUInt32LittleEndian(data, sample.Id);
            data[4] = sample.Definition;
            data[5] = sample.Component;
            data[6] = (byte)((sample.World ? 1 : 0) | (sample.Blink ? 2 : 0));
            data[7] = sample.BlinkPhase;
            GameplayWire.WriteFloat(data.Slice(8), sample.Position.x);
            GameplayWire.WriteFloat(data.Slice(12), sample.Position.y);
            GameplayWire.WriteFloat(data.Slice(16), sample.Angle);
            GameplayWire.WriteFloat(data.Slice(20), sample.Intensity);
            GameplayWire.WriteFloat(data.Slice(24), sample.Scale.x);
            GameplayWire.WriteFloat(data.Slice(28), sample.Scale.y);
            data[32] = sample.Color.r;
            data[33] = sample.Color.g;
            data[34] = sample.Color.b;
            data[35] = sample.Color.a;
        }
        return message;
    }

    internal static bool TryRead(byte[] message, Guid session, out ulong player, out uint sequence, out LightSample[] samples)
    {
        player = 0;
        sequence = 0;
        samples = Array.Empty<LightSample>();
        if (message.Length < HeaderSize || !GameplayWire.HasHeader(message, MessageKind.LightSnapshot, session))
        {
            return false;
        }

        int count = message[29];
        player = BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(17));
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(25));
        if (player == 0 || count > MaxSources || message.Length != HeaderSize + count * SampleSize)
        {
            return false;
        }

        samples = new LightSample[count];
        for (int index = 0; index < samples.Length; index++)
        {
            ReadOnlySpan<byte> data = message.AsSpan(HeaderSize + index * SampleSize);
            var sample = new LightSample
            {
                Id = BinaryPrimitives.ReadUInt32LittleEndian(data),
                Definition = data[4],
                Component = data[5],
                World = (data[6] & 1) != 0,
                Blink = (data[6] & 2) != 0,
                BlinkPhase = data[7],
                Position = new Vector2(GameplayWire.ReadFloat(data.Slice(8)), GameplayWire.ReadFloat(data.Slice(12))),
                Angle = GameplayWire.ReadFloat(data.Slice(16)),
                Intensity = GameplayWire.ReadFloat(data.Slice(20)),
                Scale = new Vector2(GameplayWire.ReadFloat(data.Slice(24)), GameplayWire.ReadFloat(data.Slice(28))),
                Color = new Color32(data[32], data[33], data[34], data[35])
            };
            if (sample.Id == 0 || sample.Definition == 0 || sample.Component >= 16 || data[6] > 3 ||
                !GameplayWire.IsCoordinate(sample.Position.x) || !GameplayWire.IsCoordinate(sample.Position.y) ||
                !GameplayWire.InRange(sample.Angle, 360) || sample.Intensity is < 0f or > 16f || !float.IsFinite(sample.Intensity) ||
                !GameplayWire.InRange(sample.Scale.x, 8) || !GameplayWire.InRange(sample.Scale.y, 8))
            {
                return false;
            }
            for (int previous = 0; previous < index; previous++)
            {
                if (samples[previous].Id == sample.Id)
                {
                    return false;
                }
            }
            samples[index] = sample;
        }
        return true;
    }
}
