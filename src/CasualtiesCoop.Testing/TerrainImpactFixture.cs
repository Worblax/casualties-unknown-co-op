using HarmonyLib;
using UnityEngine;

namespace CasualtiesCoop.Testing;

internal sealed class TerrainImpactFixture : IDisposable
{
    private const float FixtureDamage = 0.25f;
    private const float ObservationRadius = 6f;
    private const int MaxObservedSounds = 128;
    private static TerrainImpactFixture? active;
    private readonly List<(AudioClip Clip, Vector2 Position)> sounds = new();
    private Harmony? hook;
    private AudioClip? predictedClip;
    private Vector2 predictedOrigin;

    internal void RememberExisting()
    {
        if (hook is null)
        {
            hook = new Harmony("casualtiesunknown.coop.testing.terrain-impact");
            var signature = new[] { typeof(AudioClip), typeof(Vector2), typeof(bool), typeof(bool), typeof(Transform),
                typeof(float), typeof(float), typeof(bool), typeof(bool) };
            hook.Patch(AccessTools.Method(typeof(Sound), nameof(Sound.Play), signature),
                postfix: new HarmonyMethod(typeof(TerrainImpactFixture), nameof(ObserveSound)));
        }
        sounds.Clear();
        predictedClip = null;
        active = this;
    }

    private static void ObserveSound(Vector2 pos, AudioSource? __result)
    {
        if (active is { } fixture && __result && __result!.clip && fixture.sounds.Count < MaxObservedSounds)
        {
            fixture.sounds.Add((__result.clip, pos));
        }
    }

    internal bool TryNativeGuestImpact(Body body, out object evidence)
    {
        WorldGeneration world = WorldGeneration.world;
        Vector2 actor = body.transform.position;
        Vector2Int center = world.WorldToBlockPos(actor);
        if (!FindTarget(world, center, out Vector2Int target, out string resource))
        {
            evidence = new { reason = "no intact audible surface near guest", x = actor.x, y = actor.y };
            return false;
        }

        predictedClip = Resources.Load<AudioClip>("Sounds/" + resource);
        predictedOrigin = world.BlockToWorldPos(target);
        int previous = sounds.Count;
        ushort block = world.GetBlock(target);
        world.DamageBlock(target, FixtureDamage);
        bool heard = predictedClip && sounds.Skip(previous).Any(sound => sound.Clip == predictedClip && sound.Position == predictedOrigin);
        bool awaitingAuthority = world.GetBlock(target) == block && world.GetBlockDamage(target) is null;
        evidence = new { resource, clip = heard ? predictedClip!.name : null, awaitingAuthority, x = predictedOrigin.x, y = predictedOrigin.y };
        return heard && awaitingAuthority;
    }

    internal bool TryObserveHostImpact(Vector2 actor, out object evidence)
    {
        WorldGeneration world = WorldGeneration.world;
        foreach (var sound in sounds)
        {
            Vector2Int cell = world.WorldToBlockPos(sound.Position);
            if ((sound.Position - actor).sqrMagnitude > ObservationRadius * ObservationRadius ||
                sound.Position != world.BlockToWorldPos(cell) ||
                world.GetBlockDamage(cell) is not { } crack || crack.damage < FixtureDamage)
            {
                continue;
            }

            string? resource = world.GetBlockInfo(world.GetBlock(cell))?.hitsound;
            if (string.IsNullOrEmpty(resource) || Resources.Load<AudioClip>("Sounds/" + resource) != sound.Clip) continue;
            evidence = new { resource, clip = sound.Clip.name, damage = crack.damage,
                x = sound.Position.x, y = sound.Position.y };
            return true;
        }

        evidence = new { observedSounds = sounds.Count };
        return false;
    }

    internal bool ConfirmSingleGuestImpact(out object evidence)
    {
        int count = sounds.Count(sound => sound.Clip == predictedClip && sound.Position == predictedOrigin);
        evidence = new { count, x = predictedOrigin.x, y = predictedOrigin.y };
        return count == 1;
    }

    private static bool FindTarget(WorldGeneration world, Vector2Int center, out Vector2Int target, out string resource)
    {
        for (int distance = 1; distance <= 4; distance++)
        {
            for (int y = -distance; y <= distance; y++)
            {
                for (int x = -distance; x <= distance; x++)
                {
                    var cell = new Vector2Int(center.x + x, center.y + y);
                    if (cell.x < 0 || cell.x >= world.width || cell.y < 0 || cell.y >= world.height ||
                        world.GetBlockInfo(world.GetBlock(cell)) is not { health: > FixtureDamage } surface ||
                        string.IsNullOrEmpty(surface.hitsound) || world.GetBlockDamage(cell) is not null)
                    {
                        continue;
                    }

                    target = cell;
                    resource = surface.hitsound;
                    return true;
                }
            }
        }
        target = default;
        resource = "";
        return false;
    }

    public void Dispose()
    {
        if (ReferenceEquals(active, this)) active = null;
        hook?.UnpatchSelf();
    }
}
