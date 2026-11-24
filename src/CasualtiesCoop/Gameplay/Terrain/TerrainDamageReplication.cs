using System;
using System.Collections.Generic;
using System.Linq;
using CasualtiesCoop.Core;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Terrain;

internal sealed class TerrainDamageReplication
{
    private const int HistoryLimit = 4096;
    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly PlayerPositionLookup position;
    private readonly Action<WorldGeneration, Vector2Int, ushort, float> project;
    private readonly Dictionary<Vector2Int, (ushort Block, float Damage, uint Revision)> changes = new();
    private readonly Dictionary<ulong, (uint Operation, float Window, int Count)> hits = new();
    private readonly Dictionary<ulong, Queue<byte[]>> snapshots = new();
    private uint nextHit;
    private uint revision;
    private float nextSync;
    private bool applying;

    internal TerrainDamageReplication(SessionPeers peers, IMonotonicClock clock, PlayerPositionLookup position,
        Action<WorldGeneration, Vector2Int, ushort, float>? project = null)
    {
        this.peers = peers;
        this.clock = clock;
        this.position = position;
        this.project = project ?? Project;
    }

    internal static void Install(Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(WorldGeneration), nameof(WorldGeneration.DamageBlock),
            new[] { typeof(Vector2Int), typeof(float), typeof(bool), typeof(bool), typeof(bool) }),
        prefix: new HarmonyMethod(typeof(TerrainDamageReplication), nameof(BeforeDamage)));

    private static bool BeforeDamage(WorldGeneration __instance, Vector2Int pos, float dmg, bool hitSound, bool bonusMetal,
        bool ignoreLoot) => CoopRuntime.ActiveWorld is not { } world ||
        world.Terrain.BeforeNativeDamage(__instance, pos, dmg, hitSound, bonusMetal, ignoreLoot);

    private bool BeforeNativeDamage(WorldGeneration world, Vector2Int cell, float damage, bool sound, bool metal, bool ignoreLoot)
    {
        if (applying || !world || world != WorldGeneration.world || !world.worldExists || world.generatingWorld)
        {
            return true;
        }
        if (!InBounds(world, cell) || !float.IsFinite(damage) || damage <= 0 || world.GetBlock(cell) == 0)
        {
            return peers.IsHost;
        }
        if (peers.IsHost)
        {
            ApplyHit(world, cell, damage, sound, metal, ignoreLoot);
        }
        else if (damage <= 100000)
        {
            byte flags = (byte)((sound ? 1 : 0) | (metal ? 2 : 0));
            peers.Send(peers.Host, SandboxGameplayWire.WriteTerrain(peers.SessionId, MessageKind.TerrainHit,
                cell.x, cell.y, world.GetBlock(cell), ++nextHit, damage, flags));
        }
        return false;
    }

    internal static bool Handles(MessageKind kind) => kind is MessageKind.TerrainHit or MessageKind.TerrainState or MessageKind.TerrainSync;

    internal bool Receive(ulong sender, byte[] bytes)
    {
        var kind = (MessageKind)bytes[0];
        if (peers.IsHost ? sender == peers.LocalPlayer || !peers.TryGetPlayer(sender, out _) : sender != peers.Host)
        {
            return false;
        }
        if (peers.IsHost && kind == MessageKind.TerrainSync)
        {
            if (bytes.Length == 17 && GameplayWire.HasHeader(bytes, kind, peers.SessionId) && !snapshots.ContainsKey(sender))
            {
                snapshots.Add(sender, new Queue<byte[]>(changes.Select(pair => Encode(pair.Key, pair.Value))));
            }
            return true;
        }
        if (kind != (peers.IsHost ? MessageKind.TerrainHit : MessageKind.TerrainState))
        {
            return false;
        }
        var world = WorldGeneration.world;
        if (!world || !world.worldExists || world.generatingWorld ||
            !SandboxGameplayWire.TryReadTerrain(bytes, peers.SessionId, kind, out var tile))
        {
            return true;
        }
        var cell = new Vector2Int(tile.X, tile.Y);
        if (!InBounds(world, cell))
        {
            return true;
        }
        if (peers.IsHost)
        {
            if (world.GetBlock(cell) == tile.Block && position(sender, out Vector2 actor) &&
                (actor - world.BlockToWorldPos(cell)).sqrMagnitude <= 16 * 16 && AllowHit(sender, tile.Sequence))
            {
                ApplyHit(world, cell, tile.Damage, (tile.Flags & 1) != 0, (tile.Flags & 2) != 0, ignoreLoot: false);
            }
        }
        else if (!changes.TryGetValue(cell, out var last) || tile.Sequence > last.Revision)
        {
            project(world, cell, tile.Block, tile.Damage);
            Remember(cell, (tile.Block, tile.Damage, tile.Sequence));
        }
        return true;
    }

    internal void Tick()
    {
        if (!peers.IsHost)
        {
            if (clock.Now >= nextSync)
            {
                nextSync = clock.Now + 8f;
                peers.Send(peers.Host, GameplayWire.NewFrame(MessageKind.TerrainSync, peers.SessionId, 17));
            }
            return;
        }

        int budget = 12;
        var participants = peers.Players;
        for (int index = 0; index < participants.Count && budget > 0; index++)
        {
            ulong player = participants[index].SteamId;
            if (!snapshots.TryGetValue(player, out var pending))
            {
                continue;
            }
            if (!peers.IsVerified(player) || pending.Count == 0)
            {
                snapshots.Remove(player);
                continue;
            }
            int sent = 0;
            while (sent < 4 && budget > 0 && pending.Count > 0)
            {
                budget--;
                if (!peers.Send(player, pending.Peek()))
                {
                    break;
                }
                pending.Dequeue();
                sent++;
            }
        }
    }

    private bool AllowHit(ulong player, uint operation)
    {
        float now = clock.Now;
        hits.TryGetValue(player, out var previous);
        if (now - previous.Window >= 1f)
        {
            previous.Window = now;
            previous.Count = 0;
        }
        if (previous.Count >= 64 ||
            (previous.Operation != 0 && unchecked((int)(operation - previous.Operation)) <= 0))
        {
            return false;
        }
        hits[player] = (operation, previous.Window, previous.Count + 1);
        return true;
    }

    internal void ResetPeer(ulong player)
    {
        hits.Remove(player);
        snapshots.Remove(player);
        if (!peers.IsHost && player == peers.Host)
        {
            nextSync = 0;
        }
    }

    internal void ApplyHit(WorldGeneration world, Vector2Int cell, float damage, bool sound, bool metal, bool ignoreLoot)
    {
        BlockDamage? oldest = world.blockDamages.Count == 128 ? world.blockDamages[0] : null;
        applying = true;
        try
        {
            world.DamageBlock(cell, damage, sound, metal, ignoreLoot);
        }
        finally
        {
            applying = false;
        }
        if (oldest is not null && oldest.pos != cell && !world.blockDamages.Contains(oldest))
        {
            Publish(world, oldest.pos);
        }
        Publish(world, cell);
    }

    private void Publish(WorldGeneration world, Vector2Int cell)
    {
        ushort block = world.GetBlock(cell);
        float damage = Mathf.Clamp(world.GetBlockDamage(cell)?.damage ?? 0, 0, 100000);
        if (changes.TryGetValue(cell, out var current) && current.Block == block && current.Damage == damage)
        {
            return;
        }
        var entry = (block, damage, ++revision);
        Remember(cell, entry);
        peers.Broadcast(Encode(cell, entry), reliable: true);
    }

    private void Remember(Vector2Int cell, (ushort Block, float Damage, uint Revision) entry)
    {
        if (changes.Count < HistoryLimit || changes.ContainsKey(cell))
        {
            changes[cell] = entry;
        }
    }

    internal static void Project(WorldGeneration world, Vector2Int cell, ushort block, float damage)
    {
        BlockDamage? cracked = world.GetBlockDamage(cell);
        if (damage == 0 || block == 0)
        {
            if (cracked is not null)
            {
                cracked.DestroySprite();
                world.blockDamages.Remove(cracked);
            }
            if (block == 0 && world.GetBlock(cell) != 0)
            {
                world.SetBlock(cell, 0);
            }
            return;
        }
        if (world.GetBlock(cell) != block)
        {
            return;
        }
        if (cracked is null)
        {
            if (world.blockDamages.Count >= 128)
            {
                world.blockDamages[0].DestroySprite();
                world.blockDamages.RemoveAt(0);
            }
            cracked = new BlockDamage { pos = cell };
            world.blockDamages.Add(cracked);
        }
        cracked.damage = damage;
        cracked.UpdateSprite();
    }

    private byte[] Encode(Vector2Int cell, (ushort Block, float Damage, uint Revision) entry) =>
        SandboxGameplayWire.WriteTerrain(peers.SessionId, MessageKind.TerrainState, cell.x, cell.y, entry.Block,
            entry.Revision, entry.Damage);

    private static bool InBounds(WorldGeneration world, Vector2Int cell) =>
        cell.x >= 0 && cell.x < world.width && cell.x <= ushort.MaxValue &&
        cell.y >= 0 && cell.y < world.height && cell.y <= ushort.MaxValue;
}
