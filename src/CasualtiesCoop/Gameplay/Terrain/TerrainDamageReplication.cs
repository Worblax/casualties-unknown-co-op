using System.Collections.Generic;
using System.Reflection;
using CasualtiesCoop.Core;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Terrain;

internal sealed class TerrainDamageReplication
{
    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly PlayerPositionLookup position;
    private readonly WorldChunkReplication chunks;
    private readonly Dictionary<ulong, (uint Operation, float Window, int Count)> hits = new();
    private uint nextHit;
    private bool applying;

    internal TerrainDamageReplication(SessionPeers peers, IMonotonicClock clock, PlayerPositionLookup position, WorldChunkReplication chunks)
    {
        this.peers = peers;
        this.clock = clock;
        this.position = position;
        this.chunks = chunks;
    }

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(WorldGeneration), nameof(WorldGeneration.DamageBlock),
            new[] { typeof(Vector2Int), typeof(float), typeof(bool), typeof(bool), typeof(bool) }),
            prefix: Hook(nameof(BeforeDamage)));
        harmony.Patch(AccessTools.Method(typeof(WorldGeneration), nameof(WorldGeneration.SetBlock),
            new[] { typeof(Vector2Int), typeof(ushort) }), prefix: Hook(nameof(AllowWrite)), postfix: Hook(nameof(AfterSetBlock)));
        harmony.Patch(AccessTools.Method(typeof(WorldGeneration), nameof(WorldGeneration.SetBlockNoUpdate),
            new[] { typeof(Vector2Int), typeof(ushort) }), prefix: Hook(nameof(AllowWrite)), postfix: Hook(nameof(AfterSetBlock)));
        harmony.Patch(AccessTools.Method(typeof(WorldGeneration), nameof(WorldGeneration.ClearBlockDamages)),
            prefix: Hook(nameof(BeforeClearDamage)));
    }

    private static HarmonyMethod Hook(string method) => new(typeof(TerrainDamageReplication), method);

    private static bool BeforeDamage(WorldGeneration __instance, Vector2Int pos, float dmg, bool hitSound, bool bonusMetal,
        bool ignoreLoot) => CoopRuntime.ActiveWorld is not { SceneReady: true } session ||
        session.Terrain.BeforeNativeDamage(__instance, pos, dmg, hitSound, bonusMetal, ignoreLoot);

    private static bool AllowWrite(WorldGeneration __instance) =>
        CoopRuntime.ActiveWorld is not { SceneReady: true } session ||
        !Live(__instance) || session.Terrain.peers.IsHost || session.WorldChunks.Applying;

    private static void AfterSetBlock(WorldGeneration __instance, Vector2Int pos, MethodBase __originalMethod)
    {
        if (CoopRuntime.ActiveWorld is not { SceneReady: true } session || !session.Terrain.peers.IsHost || !Live(__instance)) return;
        int inset = __originalMethod.Name == nameof(WorldGeneration.SetBlockNoUpdate) ? 2 : 1;
        session.WorldChunks.MarkChanged(new Vector2Int(Mathf.Clamp(pos.x, 0, (int)__instance.width - inset),
            Mathf.Clamp(pos.y, 0, (int)__instance.height - inset)));
    }

    private static bool BeforeClearDamage(WorldGeneration __instance)
    {
        if (CoopRuntime.ActiveWorld is not { SceneReady: true } session || !Live(__instance)) return true;
        if (!session.Terrain.peers.IsHost) return session.WorldChunks.Applying;
        foreach (BlockDamage crack in __instance.blockDamages) session.WorldChunks.MarkChanged(crack.pos);
        return true;
    }

    private bool BeforeNativeDamage(WorldGeneration world, Vector2Int cell, float damage, bool sound, bool metal, bool ignoreLoot)
    {
        if (applying || !Live(world)) return true;
        if (!InBounds(world, cell) || !float.IsFinite(damage) || damage <= 0 || world.GetBlock(cell) == 0)
            return peers.IsHost;
        if (peers.IsHost)
            ApplyHit(world, cell, damage, sound, metal, ignoreLoot);
        else if (chunks.Ready && damage <= 100000)
        {
            byte flags = (byte)((sound ? 1 : 0) | (metal ? 2 : 0));
            peers.Send(peers.Host, SandboxGameplayWire.WriteTerrainHit(peers.SessionId,
                cell.x, cell.y, world.GetBlock(cell), ++nextHit, damage, flags));
        }
        return false;
    }

    internal bool Receive(ulong sender, byte[] frame)
    {
        if (!peers.IsHost || sender == peers.LocalPlayer || !peers.TryGetPlayer(sender, out _)) return false;
        var world = WorldGeneration.world;
        if (!Live(world) || !SandboxGameplayWire.TryReadTerrainHit(frame, peers.SessionId, out var tile)) return true;

        var cell = new Vector2Int(tile.X, tile.Y);
        if (InBounds(world, cell) && world.GetBlock(cell) == tile.Block && position(sender, out Vector2 actor) &&
            (actor - world.BlockToWorldPos(cell)).sqrMagnitude <= 16 * 16 && AllowHit(sender, tile.Sequence))
            ApplyHit(world, cell, tile.Damage, (tile.Flags & 1) != 0, (tile.Flags & 2) != 0, ignoreLoot: false);
        return true;
    }

    private bool AllowHit(ulong player, uint operation)
    {
        float now = clock.Now;
        hits.TryGetValue(player, out var previous);
        if (now - previous.Window >= 1f) { previous.Window = now; previous.Count = 0; }
        if (previous.Count >= 64 ||
            (previous.Operation != 0 && unchecked((int)(operation - previous.Operation)) <= 0)) return false;
        hits[player] = (operation, previous.Window, previous.Count + 1);
        return true;
    }

    internal void ResetPeer(ulong player) => hits.Remove(player);

    internal void ApplyHit(WorldGeneration world, Vector2Int cell, float damage, bool sound, bool metal, bool ignoreLoot)
    {
        BlockDamage? oldest = world.blockDamages.Count >= 128 ? world.blockDamages[0] : null;
        applying = true;
        try { world.DamageBlock(cell, damage, sound, metal, ignoreLoot); }
        finally { applying = false; }
        if (oldest is not null && oldest.pos != cell && !world.blockDamages.Contains(oldest)) chunks.MarkChanged(oldest.pos);
        chunks.MarkChanged(cell);
    }

    private static bool Live(WorldGeneration world) => world && world == WorldGeneration.world && world.worldExists && !world.generatingWorld;

    private static bool InBounds(WorldGeneration world, Vector2Int cell) =>
        cell.x >= 0 && cell.x < world.width && cell.x <= ushort.MaxValue &&
        cell.y >= 0 && cell.y < world.height && cell.y <= ushort.MaxValue;
}
