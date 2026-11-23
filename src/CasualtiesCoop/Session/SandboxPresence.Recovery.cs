using System;
using System.Collections.Generic;
using CasualtiesCoop.Networking;
using Steamworks;
using UnityEngine;

namespace CasualtiesCoop.Session;

internal sealed partial class SandboxPresence
{
    private const float RecoveryRetry = 1f;
    private readonly HashSet<CSteamID> returningPeers = new();
    private readonly Dictionary<CSteamID, (SandboxResumeState State, byte[] Message)> recoveryReceipts = new();
    private Guid recoveryAttempt;
    private SandboxResumeState? recoveryState;
    private float nextRecoverySend;
    private float nextRecoveryNotice;
    private bool positionRestored;
    private bool recoveryAcknowledged;

    internal bool WaitingForRecovery => recoveryAttempt != Guid.Empty;
    internal RoomLaunchPlan Plan => plan;

    private void OnRecoveryPeerChanged(CSteamID peer, bool connected)
    {
        if (lobby.Owner == lobby.LocalUser)
        {
            if (plan.TryGetPlayer(peer.m_SteamID, out _))
            {
                returningPeers.Add(peer);
                recoveryReceipts.Remove(peer);
            }
        }
        else if (peer == lobby.Owner && !connected)
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

    internal bool ReceiveRecovery(CSteamID peer, byte[] bytes)
    {
        RoomFrame frame = (RoomFrame)bytes[0];
        if (lobby.IsHost)
        {
            if (!plan.TryGetPlayer(peer.m_SteamID, out _) || frame is not (RoomFrame.SandboxResumeRequest or RoomFrame.SandboxResumeApplied))
            {
                return false;
            }

            Guid attempt = SandboxResumeWire.ReadReference(bytes, frame, plan.Id);
            if (attempt != Guid.Empty && Ready)
            {
                if (frame == RoomFrame.SandboxResumeRequest)
                {
                    OfferRecovery(peer, attempt);
                }
                else if (recoveryReceipts.TryGetValue(peer, out var receipt) && receipt.State.Attempt == attempt &&
                    lobby.Link.Send(peer, SandboxResumeWire.WriteReference(RoomFrame.SandboxResumeCommitted, plan.Id, attempt)) &&
                    returningPeers.Remove(peer))
                {
                    log.LogInfo("A returning sandbox guest applied the snapshot. Control was released.");
                }
            }
            return true;
        }

        if (peer != lobby.Owner)
        {
            return false;
        }

        if (frame == RoomFrame.SandboxResumeState && SandboxResumeWire.TryReadState(bytes, plan.Id, out SandboxResumeState state) &&
            state.Attempt == recoveryAttempt && recoveryState is null)
        {
            RestoreFrom(state);
        }
        else if (frame == RoomFrame.SandboxResumeCommitted && recoveryAcknowledged && WaitingForRecovery &&
            SandboxResumeWire.ReadReference(bytes, frame, plan.Id) == recoveryAttempt)
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

    private void OfferRecovery(CSteamID peer, Guid attempt)
    {
        if (!returningPeers.Contains(peer))
        {
            return;
        }

        if (!recoveryReceipts.TryGetValue(peer, out var receipt) || receipt.State.Attempt != attempt)
        {
            remotes.TryGetValue(peer.m_SteamID, out RemotePlayer? remote);
            SandboxPosition previous = remote?.Pose ?? default;
            bool hasPosition = previous.Player != 0 && IsWorldPosition(new Vector2(previous.X, previous.Y));
            SandboxResumeState state = new(plan, attempt, previous.Sequence, remote?.SoundSequence ?? 0,
                lights.LastSequence(peer.m_SteamID), Items.LastOperation(peer.m_SteamID), Fluids.LastDrink(peer),
                previous.X, previous.Y, hasPosition);
            if (SandboxResumeWire.WriteState(state) is not { } message)
            {
                return;
            }

            receipt = (state, message);
            recoveryReceipts[peer] = receipt;
            Fluids.ResendNearby(peer);
            log.LogInfo("Preparing the reserved sandbox seat for a returning guest.");
        }
        lobby.Link.Send(peer, receipt.Message);
    }

    internal void RestoreFrom(SandboxResumeState state)
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
            SendRecovery(RoomFrame.SandboxResumeRequest, now);
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
            recoveryAcknowledged |= SendRecovery(RoomFrame.SandboxResumeApplied, now);
        }
    }

    private bool SendRecovery(RoomFrame frame, float now)
    {
        if (now >= nextRecoverySend)
        {
            nextRecoverySend = now + RecoveryRetry;
            return lobby.Link.Send(lobby.Owner, SandboxResumeWire.WriteReference(frame, plan.Id, recoveryAttempt));
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
