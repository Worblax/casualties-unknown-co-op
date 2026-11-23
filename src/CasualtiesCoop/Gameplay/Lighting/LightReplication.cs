using CasualtiesCoop.Gameplay.Characters;
using System;
using System.Collections.Generic;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CasualtiesCoop.Gameplay.Lighting;

internal sealed class LightReplication : IDisposable
{
    private const float SendInterval = 0.10f;
    private const float SourceTimeout = 1.2f;

    private sealed class OwnerLights
    {
        internal uint Sequence;
        internal float LastSeen;
        internal readonly Dictionary<uint, RemoteLight> Sources = new();
    }

    private readonly SessionPeers peers;
    private readonly PlayerPositionLookup playerPosition;
    private readonly Action<byte[]> sendLocal;
    private readonly Action<byte[], ulong> relay;
    private readonly LightPrefabCatalog catalog = new();
    private readonly TerrainShadowCasters shadows = new();
    private readonly LocalLightVisibility visibility = new();
    private readonly Dictionary<ulong, OwnerLights> remote = new();
    private readonly List<Light2D> captured = new();
    private readonly List<Light2D> projected = new();
    private readonly List<uint> expired = new();
    private readonly LightSample[] outbound = new LightSample[LightWire.MaxSources];
    private readonly HashSet<uint> present = new();
    private Texture2D? glintTexture;
    private Sprite? glintSprite;
    private uint nextSequence;
    private float nextSend;
    private float emptyUntil;

    internal LightReplication(SessionPeers peers, PlayerPositionLookup playerPosition,
        Action<byte[]> sendLocal, Action<byte[], ulong> relay)
    {
        this.peers = peers;
        this.playerPosition = playerPosition;
        this.sendLocal = sendLocal;
        this.relay = relay;
    }

    internal uint LastSequence(ulong player) => remote.TryGetValue(player, out OwnerLights owner) ? owner.Sequence : 0;

    internal void AdvanceLocalSequence(uint confirmed)
    {
        if (nextSequence == 0 || unchecked((int)(confirmed - nextSequence)) > 0)
        {
            nextSequence = confirmed;
        }
    }

    internal void Tick(Body body)
    {
        WorldGeneration world = WorldGeneration.world;
        if (!world || !world.worldExists || world.generatingWorld)
        {
            return;
        }

        float now = Time.unscaledTime;
        Vector2 observer = body.transform.position;
        if (now >= nextSend)
        {
            nextSend = now + SendInterval;
            SendLights(body, now);
        }

        projected.Clear();
        foreach (OwnerLights owner in remote.Values)
        {
            if (now - owner.LastSeen > SourceTimeout)
            {
                Clear(owner);
                continue;
            }

            foreach (RemoteLight source in owner.Sources.Values)
            {
                source.Tick(observer, world);
                if (source.Light.isActiveAndEnabled)
                {
                    projected.Add(source.Light);
                }
            }
        }

        shadows.Tick(world, observer, projected);
        visibility.Refresh();
    }

    internal bool BodyVisible(Vector2 observer, CharacterPose pose)
    {
        WorldGeneration world = WorldGeneration.world;
        if (!world || !world.worldExists || world.generatingWorld)
        {
            return false;
        }

        Vector2 center = new(pose.X, pose.Y);
        if (visibility.BodyVisible(world, observer, center))
        {
            return true;
        }
        // A lit head can be visible when the center of a ragdoll is still in shadow.
        if (pose.Limbs is { Length: > 0 } limbs && limbs[0].Visible)
        {
            Vector2 head = center + new Vector2(limbs[0].X, limbs[0].Y);
            return visibility.BodyVisible(world, observer, head);
        }
        return false;
    }

    private void SendLights(Body body, float now)
    {
        int count = 0;
        present.Clear();
        foreach (Item item in body.GetAllItems())
        {
            Capture(item, false, ref count);
        }
        foreach (Item item in body.GetAllWearables())
        {
            Capture(item, false, ref count);
        }

        if (peers.IsHost && count < outbound.Length)
        {
            foreach (Item item in Item.allItems)
            {
                if (item && !item.transform.parent && LightPrefabCatalog.DefinitionOf(item) != 0 &&
                    NearParticipant(item.transform.position))
                {
                    Capture(item, true, ref count);
                    if (count == outbound.Length)
                    {
                        break;
                    }
                }
            }
        }

        if (count > 0)
        {
            emptyUntil = now + SourceTimeout;
        }
        if (count > 0 || now < emptyUntil)
        {
            sendLocal(LightWire.Write(peers.SessionId, peers.LocalPlayer, ++nextSequence, outbound.AsSpan(0, count)));
        }
    }

    private bool NearParticipant(Vector2 source)
    {
        foreach (LaunchPlayer player in peers.Players)
        {
            if (playerPosition(player.SteamId, out Vector2 at) && (at - source).sqrMagnitude < 110f * 110f)
            {
                return true;
            }
        }
        return false;
    }

    private void Capture(Item item, bool world, ref int count)
    {
        if (!item || count >= outbound.Length)
        {
            return;
        }

        byte definition = LightPrefabCatalog.DefinitionOf(item);
        if (definition == 0)
        {
            return;
        }
        item.GetComponentsInChildren(true, captured);
        bool blink = item.id == "flashlight" && item.TryGetComponent(out CustomItemBehaviour behaviour) && behaviour.state == 3;
        for (int index = 0; index < captured.Count && index < 16 && count < outbound.Length; index++)
        {
            Light2D light = captured[index];
            if (!light || !light.isActiveAndEnabled || (!blink && light.intensity <= 0.01f) ||
                !catalog.Template(definition, (byte)index))
            {
                continue;
            }

            uint id = unchecked((uint)light.GetInstanceID());
            if (id == 0 || !present.Add(id))
            {
                continue;
            }

            Transform at = light.transform;
            float phase = Mathf.Repeat(Time.time * 18f, 2f) * 0.5f;
            outbound[count++] = new LightSample
            {
                Id = id,
                Definition = definition,
                Component = (byte)index,
                World = world,
                Blink = blink,
                BlinkPhase = (byte)Mathf.RoundToInt(phase * 255f),
                Position = at.position,
                Angle = at.eulerAngles.z,
                Intensity = blink ? 1f : Mathf.Clamp(light.intensity, 0f, 16f),
                Scale = new Vector2(Mathf.Clamp(at.lossyScale.x, -8f, 8f), Mathf.Clamp(at.lossyScale.y, -8f, 8f)),
                Color = light.color
            };
        }
    }

    internal bool Receive(ulong sender, byte[] message)
    {
        if (!LightWire.TryRead(message, peers.SessionId, out ulong player, out uint sequence, out LightSample[] samples))
        {
            return true;
        }

        if (player == peers.LocalPlayer || !peers.TryGetPlayer(player, out _) ||
            (peers.IsHost ? sender != player : sender != peers.Host))
        {
            return false;
        }

        if (peers.IsHost && samples.Length > 0)
        {
            if (!playerPosition(player, out Vector2 at))
            {
                return true;
            }

            foreach (LightSample sample in samples)
            {
                if (sample.World || (sample.Position - at).sqrMagnitude > 18f * 18f)
                {
                    return true;
                }
            }
        }

        if (!remote.TryGetValue(player, out OwnerLights owner))
        {
            owner = new OwnerLights();
            remote.Add(player, owner);
        }
        else if (unchecked((int)(sequence - owner.Sequence)) <= 0)
        {
            return true;
        }

        float now = Time.unscaledTime;
        owner.Sequence = sequence;
        owner.LastSeen = now;
        present.Clear();
        foreach (LightSample sample in samples)
        {
            Light2D? template = catalog.Template(sample.Definition, sample.Component);
            if (!template)
            {
                continue;
            }

            present.Add(sample.Id);
            if (owner.Sources.TryGetValue(sample.Id, out RemoteLight? source) &&
                !source.Matches(sample.Definition, sample.Component))
            {
                source.Dispose();
                owner.Sources.Remove(sample.Id);
                source = null;
            }
            if (source is null)
            {
                source = new RemoteLight(template!, GlintSprite());
                owner.Sources.Add(sample.Id, source);
            }
            source.Receive(sample, now);
        }

        expired.Clear();
        foreach (uint id in owner.Sources.Keys)
        {
            if (!present.Contains(id))
            {
                expired.Add(id);
            }
        }
        foreach (uint id in expired)
        {
            owner.Sources[id].Dispose();
            owner.Sources.Remove(id);
        }

        if (peers.IsHost)
        {
            relay(message, sender);
        }
        return true;
    }

    private Sprite GlintSprite()
    {
        if (glintSprite)
        {
            return glintSprite!;
        }

        const int size = 12;
        glintTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(size * 0.5f, size * 0.5f));
                float alpha = 1f - Mathf.InverseLerp(1.5f, size * 0.5f, distance);
                glintTexture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        glintTexture.Apply(false, false);
        glintSprite = Sprite.Create(glintTexture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 32f, 0, SpriteMeshType.FullRect);
        return glintSprite;
    }

    private static void Clear(OwnerLights owner)
    {
        foreach (RemoteLight light in owner.Sources.Values)
        {
            light.Dispose();
        }
        owner.Sources.Clear();
    }

    public void Dispose()
    {
        foreach (OwnerLights owner in remote.Values)
        {
            Clear(owner);
        }
        shadows.Dispose();
        if (glintSprite)
        {
            UnityEngine.Object.Destroy(glintSprite);
            UnityEngine.Object.Destroy(glintTexture);
        }
    }
}
