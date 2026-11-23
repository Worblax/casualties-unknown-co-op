using System;
using System.Collections.Generic;
using CasualtiesCoop.Gameplay.Characters;
using CasualtiesCoop.Gameplay.Recovery;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;
using UnityEngine;

namespace CasualtiesCoop.Gameplay;

internal sealed partial class WorldSession
{
    private const float RecoveryRetry = 1f;
    private readonly HashSet<ulong> returningPeers = new();
    private readonly Dictionary<ulong, (SessionRejoinState State, byte[] Message)> recoveryReceipts = new();
    private Guid recoveryAttempt;
    private SessionRejoinState? recoveryState;
    private float nextRecoverySend;
    private float nextRecoveryNotice;
    private bool positionRestored;
    private bool recoveryAcknowledged;

    internal bool WaitingForRecovery => recoveryAttempt != Guid.Empty;
    internal RoomLaunchPlan Plan => plan;

    private void OnRecoveryPeerChanged(ulong peer, bool connected)
    {
        if (peers.IsHost)
        {
            if (plan.TryGetPlayer(peer, out _))
            {
                returningPeers.Add(peer);
                recoveryReceipts.Remove(peer);
            }
        }
        else if (peer == peers.Host && !connected)
        {
            recoveryAttempt = Guid.NewGuid();
            recoveryState = null;
            positionRestored = false;
            recoveryAcknowledged = false;
            nextRecoverySend = 0;
            nextRecoveryNotice = 0;
            Items.BeginRecovery(0, freshBody: false);
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
            if (attempt != Guid.Empty && Ready)
            {
                if (frame == MessageKind.SessionRejoinRequest)
                {
                    OfferRecovery(peer, attempt);
                }
                else if (recoveryReceipts.TryGetValue(peer, out var receipt) && receipt.State.Attempt == attempt &&
                    peers.Send(peer, SessionRejoinWire.WriteReference(MessageKind.SessionRejoinCommitted, plan.Id, attempt)) &&
                    returningPeers.Remove(peer))
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
            state.Attempt == recoveryAttempt && recoveryState is null)
        {
            RestoreFrom(state);
        }
        else if (frame == MessageKind.SessionRejoinCommitted && recoveryAcknowledged && WaitingForRecovery &&
            SessionRejoinWire.ReadReference(bytes, frame, plan.Id) == recoveryAttempt)
        {
            recoveryAttempt = Guid.Empty;
            recoveryState = null;
            nextUpdate = 0;
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
        if (!returningPeers.Contains(peer))
        {
            return;
        }

        if (!recoveryReceipts.TryGetValue(peer, out var receipt) || receipt.State.Attempt != attempt)
        {
            remotes.TryGetValue(peer, out RemotePlayer? remote);
            CharacterPose previous = remote?.Pose ?? default;
            bool hasPosition = previous.Player != 0 && IsWorldPosition(new Vector2(previous.X, previous.Y));
            SessionRejoinState state = new(plan, attempt, previous.Sequence, remote?.SoundSequence ?? 0,
                lights.LastSequence(peer), Items.LastOperation(peer), Fluids.LastDrink(peer),
                previous.X, previous.Y, hasPosition);
            if (SessionRejoinWire.WriteState(state) is not { } message)
            {
                return;
            }

            receipt = (state, message);
            recoveryReceipts[peer] = receipt;
            Fluids.ResendNearby(peer);
            log.LogInfo("Preparing the reserved sandbox seat for a returning guest.");
        }
        peers.Send(peer, receipt.Message);
    }

    internal void RestoreFrom(SessionRejoinState state)
    {
        recoveryAttempt = state.Attempt;
        recoveryState = state;
        positionRestored = false;
        recoveryAcknowledged = false;
        nextRecoverySend = 0;
        AdvanceSequence(ref sequence, state.PoseSequence);
        AdvanceSequence(ref soundSequence, state.SoundSequence);
        lights.AdvanceLocalSequence(state.LightSequence);
        Items.BeginRecovery(state.ItemOperation, freshBody: !Ready);
        Fluids.BeginRecovery(state.DrinkOperation);
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

        float now = Time.unscaledTime;
        if (now >= nextRecoveryNotice)
        {
            nextRecoveryNotice = now + 6f;
            PlayerCamera.main.DoAlert(recoveryState is null ? "Reconnecting to the co-op host…" : "Restoring your co-op state…");
        }

        if (recoveryState is not { } state)
        {
            SendRecovery(MessageKind.SessionRejoinRequest, now);
            return;
        }

        if (state.HasPosition)
        {
            MoveToRecoveryPosition(body, new Vector2(state.X, state.Y));
            if (!positionRestored)
            {
                Transform camera = PlayerCamera.main.transform;
                camera.position = new Vector3(state.X, state.Y, camera.position.z);
            }
        }
        positionRestored = true;

        if (Items.RecoveryComplete() && (!state.HasPosition || Fluids.HasSnapshotNear(new Vector2(state.X, state.Y))))
        {
            recoveryAcknowledged |= SendRecovery(MessageKind.SessionRejoinApplied, now);
        }
    }

    private bool SendRecovery(MessageKind frame, float now)
    {
        if (now >= nextRecoverySend)
        {
            nextRecoverySend = now + RecoveryRetry;
            return peers.Send(peers.Host, SessionRejoinWire.WriteReference(frame, plan.Id, recoveryAttempt));
        }
        return false;
    }

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
