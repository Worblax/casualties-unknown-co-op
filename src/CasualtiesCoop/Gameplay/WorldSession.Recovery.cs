using System;
using CasualtiesCoop.Core;
using CasualtiesCoop.Gameplay.Characters;
using CasualtiesCoop.Gameplay.Recovery;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;
using UnityEngine;

namespace CasualtiesCoop.Gameplay;

internal sealed partial class WorldSession
{
    private readonly HostRecovery hostRecovery = new();
    private float nextRecoveryNotice;
    private bool positionRestored;
    private bool recoveryFailureShown;

    internal bool WaitingForRecovery => guestRecovery.Waiting;
    internal RoomLaunchPlan Plan => plan;

    private void OnRecoveryPeerChanged(ulong peer, bool connected)
    {
        if (!connected)
        {
            Terrain.ResetPeer(peer);
            WorldChunks.Forget(peer);
            Items.ResetPeer(peer);
        }
        if (peers.IsHost)
        {
            if (plan.TryGetPlayer(peer, out _))
            {
                hostRecovery.MarkReturning(peer);
            }
        }
        else if (peer == peers.Host && !connected)
        {
            guestRecovery.Begin(Guid.NewGuid());
            positionRestored = false;
            nextRecoveryNotice = 0;
            recoveryFailureShown = false;
            Items.SuspendForRecovery();
            Fluids.BeginRecovery(0);
        }
    }

    internal bool ReceiveRecovery(ulong peer, byte[] bytes)
    {
        MessageKind frame = (MessageKind)bytes[0];
        if (peers.IsHost)
        {
            if (!plan.TryGetPlayer(peer, out _) || frame is not (MessageKind.SessionRejoinRequest or MessageKind.SessionRejoinApplied))
            {
                return false;
            }

            Guid attempt = SessionRejoinWire.ReadReference(bytes, frame, plan.Id);
            if (attempt != Guid.Empty && SceneReady)
            {
                if (frame == MessageKind.SessionRejoinRequest)
                {
                    OfferRecovery(peer, attempt);
                }
                else if (hostRecovery.TryCommit(peer, attempt, target => peers.Send(target,
                    SessionRejoinWire.WriteReference(MessageKind.SessionRejoinCommitted, plan.Id, attempt))))
                {
                    log.LogInfo("A returning sandbox guest applied the snapshot. Control was released.");
                }
            }
            return true;
        }

        if (peer != peers.Host)
        {
            return false;
        }

        if (frame == MessageKind.SessionRejoinState && SessionRejoinWire.TryReadState(bytes, plan.Id, out SessionRejoinState state) &&
            guestRecovery.ExpectsState(state.Attempt))
        {
            RestoreFrom(state);
        }
        else if (frame == MessageKind.SessionRejoinCommitted &&
            guestRecovery.TryCommit(SessionRejoinWire.ReadReference(bytes, frame, plan.Id)))
        {
            WorldChunks.CompleteRecovery();
            log.LogInfo("Sandbox rejoin complete.");
            if (PlayerCamera.main)
            {
                PlayerCamera.main.DoAlert("Co-op connection restored.");
            }
        }
        return true;
    }

    private void OfferRecovery(ulong peer, Guid attempt)
    {
        byte[]? message = hostRecovery.Offer(peer, attempt, () => CaptureRecovery(peer, attempt), out bool created, clock.Now);
        if (message is null)
        {
            return;
        }

        if (created)
        {
            WorldChunks.Forget(peer);
            log.LogInfo("Preparing the reserved sandbox seat for a returning guest.");
        }
        peers.Send(peer, message);
    }

    private SessionRejoinState CaptureRecovery(ulong peer, Guid attempt)
    {
        remotes.TryGetValue(peer, out RemotePlayer? remote);
        CharacterPose previous = remote?.Pose ?? default;
        Vector2 at = new(previous.X, previous.Y);
        if (previous.Player == 0 || !IsWorldPosition(at)) at = initialSpawn;
        return new SessionRejoinState(plan, attempt, previous.Sequence, remote?.SoundSequence ?? 0,
            lights.LastSequence(peer), Items.LastOperation(peer), Fluids.LastDrink(peer), at.x, at.y, IsWorldPosition(at));
    }

    internal void RestoreFrom(SessionRejoinState state)
    {
        guestRecovery.RestoreFrom(state);
        positionRestored = false;
        poseSampler.Resume(state.PoseSequence);
        AdvanceSequence(ref soundSequence, state.SoundSequence);
        lights.AdvanceLocalSequence(state.LightSequence);
        Items.BeginRecovery(state.ItemOperation, freshBody: !SceneReady);
        Fluids.BeginRecovery(state.DrinkOperation);
        WorldChunks.Resume(state.Attempt);
    }

    private static void AdvanceSequence(ref uint local, uint confirmed)
    {
        if (local == 0 || unchecked((int)(confirmed - local)) > 0)
        {
            local = confirmed;
        }
    }

    private void TickRecovery(Body body)
    {
        if (!WaitingForRecovery)
        {
            return;
        }

        body.moveDir = Vector2.zero;
        if (body.rb.simulated)
        {
            body.rb.velocity = Vector2.zero;
        }

        if (guestRecovery.TimedOut)
        {
            if (guestRecovery.TryRetry(Guid.NewGuid()))
            {
                Items.SuspendForRecovery();
                WorldChunks.Suspend();
                positionRestored = false;
                nextRecoveryNotice = 0;
            }
            else
            {
                if (!recoveryFailureShown)
                {
                    recoveryFailureShown = true;
                    log.LogWarning("Co-op recovery timed out after three attempts.");
                    PlayerCamera.main.DoAlert("Co-op recovery timed out. Return to the menu and rejoin the room.");
                }
                return;
            }
        }

        float now = clock.Now;
        if (now >= nextRecoveryNotice)
        {
            nextRecoveryNotice = now + 6f;
            PlayerCamera.main.DoAlert(guestRecovery.Snapshot is null ? "Reconnecting to the co-op host…" : "Restoring your co-op state…");
        }

        if (guestRecovery.Snapshot is not { } state)
        {
            guestRecovery.Tick(snapshotApplied: false, SendRecovery);
            return;
        }

        Vector2 at = new(state.X, state.Y);
        if (state.HasPosition)
        {
            MoveToRecoveryPosition(body, at);
            if (!positionRestored)
            {
                Transform camera = PlayerCamera.main.transform;
                camera.position = new Vector3(state.X, state.Y, camera.position.z);
            }
        }
        positionRestored = true;

        guestRecovery.Tick(Items.RecoveryComplete() && WorldChunks.HasNear(state.HasPosition ? at : (Vector2)body.transform.position), SendRecovery);
    }

    private bool SendRecovery(MessageKind frame, Guid attempt) =>
        peers.Send(peers.Host, SessionRejoinWire.WriteReference(frame, plan.Id, attempt));

    private static bool IsWorldPosition(Vector2 position)
    {
        WorldGeneration world = WorldGeneration.world;
        if (!world || !world.worldExists || world.generatingWorld)
        {
            return false;
        }

        Vector2Int cell = world.WorldToBlockPos(position);
        return cell.x > 0 && cell.y > 0 && cell.x < world.width - 1 && cell.y < world.height - 1;
    }

    private static void MoveToRecoveryPosition(Body body, Vector2 position)
    {
        Vector2 delta = position - (Vector2)body.transform.position;
        if (delta.sqrMagnitude > 0.0001f)
        {
            body.transform.position += (Vector3)delta;
            body.rb.position = position;
            if (!body.standing)
            {
                foreach (Limb limb in body.limbs)
                {
                    if (limb && limb.rb && limb.rb.simulated && !limb.dismembered)
                    {
                        limb.rb.velocity = Vector2.zero;
                    }
                }
            }
        }
    }
}
