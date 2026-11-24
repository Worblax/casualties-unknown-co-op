using System.Collections.Generic;
using CasualtiesCoop.Core;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Characters;

internal sealed class ClawEffects
{
    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly PlayerPositionLookup position;
    private readonly Dictionary<ulong, (uint Sequence, float At)> applied = new();
    private GameObject? prefab;
    private uint sequence;

    internal ClawEffects(SessionPeers peers, IMonotonicClock clock, PlayerPositionLookup position)
    {
        this.peers = peers;
        this.clock = clock;
        this.position = position;
    }

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Body), nameof(Body.Attack), new[] { typeof(AttackInfo), typeof(int) }),
            prefix: new HarmonyMethod(typeof(ClawEffects), nameof(BeforeAttack)),
            postfix: new HarmonyMethod(typeof(ClawEffects), nameof(AfterAttack)));
    }

    private static void BeforeAttack(Body __instance, AttackInfo atk, out int __state)
    {
        __state = atk.unarmed && CoopRuntime.ActiveWorld is { WaitingForRecovery: false } &&
            PlayerCamera.main && PlayerCamera.main.body == __instance ? __instance.transform.childCount : -1;
    }

    private static void AfterAttack(Body __instance, int __state)
    {
        if (__state < 0 || CoopRuntime.ActiveWorld is not { } session)
        {
            return;
        }

        for (int child = __state; child < __instance.transform.childCount; child++)
        {
            Transform effect = __instance.transform.GetChild(child);
            if (effect.name == "ClawAnim(Clone)")
            {
                session.Claws.Send(effect, __instance.isRight);
                break;
            }
        }
    }

    private void Send(Transform effect, bool right)
    {
        Vector2 at = effect.position;
        byte[] frame = SandboxGameplayWire.WriteClaw(peers.SessionId, peers.LocalPlayer, ++sequence, at.x, at.y,
            effect.eulerAngles.z, right);
        if (peers.IsHost) peers.Broadcast(frame, reliable: false);
        else peers.Send(peers.Host, frame, reliable: false);
    }

    internal bool Receive(ulong sender, byte[] frame)
    {
        if (!SandboxGameplayWire.TryReadClaw(frame, peers.SessionId, out var effect))
        {
            return true;
        }
        if (!peers.AuthoredBy(sender, effect.Player))
        {
            return false;
        }
        Vector2 at = new(effect.X, effect.Y);
        float now = clock.Now;
        if (!position(effect.Player, out Vector2 origin) || (at - origin).sqrMagnitude > 64 ||
            (applied.TryGetValue(effect.Player, out var last) && (now - last.At < 0.025f ||
                (now - last.At < 2 && unchecked((int)(effect.Sequence - last.Sequence)) <= 0))))
        {
            return true;
        }
        applied[effect.Player] = (effect.Sequence, now);
        if (!prefab) prefab = Resources.Load<GameObject>("ClawAnim");
        if (prefab)
        {
            GameObject clone = Object.Instantiate(prefab!, at, Quaternion.Euler(0, 0, effect.Angle));
            clone.transform.localScale = new Vector3(effect.Right ? 1 : -1, 1, 1);
            Object.Destroy(clone, 5);
        }
        if (peers.IsHost) peers.Broadcast(frame, reliable: false, except: sender);
        return true;
    }
}
