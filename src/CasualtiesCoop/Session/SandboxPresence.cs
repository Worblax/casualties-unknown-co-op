using System;
using System.Collections.Generic;
using BepInEx.Logging;
using CasualtiesCoop.Game;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using Steamworks;
using TMPro;
using UnityEngine;

namespace CasualtiesCoop.Session;

internal sealed partial class SandboxPresence : IDisposable
{
    private const float UpdateInterval = 0.067f;
    private const float PoseTimeout = 2f;
    private const int SoundsPerSecond = 48;

    private sealed class RemotePlayer
    {
        internal SandboxPosition Pose;
        internal float UpdatedAt;
        internal uint SoundSequence;
        internal float SoundWindow;
        internal int SoundCount;
        internal RemoteBodyView? View;
    }

    private readonly SteamLobby lobby;
    private readonly RoomLaunchPlan plan;
    private readonly TMP_FontAsset font;
    private readonly ManualLogSource log;
    private readonly Dictionary<ulong, RemotePlayer> remotes = new();
    private readonly SandboxAudio audio = new();
    private readonly SandboxLights lights;
    private uint sequence;
    private uint soundSequence;
    private float nextUpdate;

    internal bool Ready { get; private set; }
    internal SandboxItems Items { get; }
    internal SandboxFluids Fluids { get; }

    internal SandboxPresence(SteamLobby lobby, RoomLaunchPlan plan, TMP_FontAsset font, ManualLogSource log)
    {
        this.lobby = lobby;
        this.plan = plan;
        this.font = font;
        this.log = log;
        Items = new SandboxItems(lobby, plan, TryGetPosition);
        Fluids = new SandboxFluids(lobby, plan, TryGetPosition);
        lights = new SandboxLights(lobby, plan, TryGetPosition, SendLocal, Broadcast);
        lobby.Link.PeerChanged += OnRecoveryPeerChanged;
    }

    internal bool IsParticipant(CSteamID player, Guid launchId) =>
        launchId == plan.Id && plan.TryGetPlayer(player.m_SteamID, out _);

    internal void Tick()
    {
        if (!PlayerCamera.main || !PlayerCamera.main.body)
        {
            return;
        }

        Ready = true;
        Body body = PlayerCamera.main.body;
        TickRecovery(body);
        float now = Time.unscaledTime;
        Vector2 position = body.transform.position;
        if (!WaitingForRecovery && now >= nextUpdate)
        {
            nextUpdate = now + UpdateInterval;
            SendLocal(SandboxWire.WritePosition(plan.Id, CapturePose(body, position)));
        }

        lights.Tick(body);
        foreach (KeyValuePair<ulong, RemotePlayer> entry in remotes)
        {
            RemotePlayer remote = entry.Value;
            float age = now - remote.UpdatedAt;
            if (age > PoseTimeout || remote.Pose.Player == 0 || !lights.BodyVisible(position, remote.Pose))
            {
                remote.View?.Hide();
                continue;
            }

            if (remote.View is null && plan.TryGetPlayer(entry.Key, out LaunchPlayer member))
            {
                remote.View = new RemoteBodyView(member.Character, SteamFriends.GetFriendPersonaName(new CSteamID(entry.Key)), font);
            }
            remote.View?.Apply(remote.Pose, age);
        }

        audio.Tick(position);
        Items.Tick(body);
        Fluids.Tick();
    }

    private SandboxPosition CapturePose(Body body, Vector2 origin)
    {
        int count = Math.Min(body.limbs.Length, SandboxWire.MaxLimbs);
        SandboxLimb[] limbs = new SandboxLimb[count];
        for (int index = 0; index < count; index++)
        {
            Limb limb = body.limbs[index];
            if (!limb || limb.dismembered)
            {
                continue;
            }

            Transform transform = limb.transform;
            Vector2 offset = (Vector2)transform.position - origin;
            Vector3 scale = transform.lossyScale;
            if (offset.sqrMagnitude <= 256f * 256f)
            {
                limbs[index] = new SandboxLimb(offset.x, offset.y, transform.eulerAngles.z,
                    Mathf.Min(8, Mathf.Abs(scale.x)), Mathf.Min(8, Mathf.Abs(scale.y)), limb.gameObject.activeInHierarchy);
            }
        }

        Vector2 velocity = body.standing || count < 3 || !body.limbs[2] ? body.rb.velocity : body.limbs[2].rb.velocity;
        return new SandboxPosition(lobby.LocalUser.m_SteamID, ++sequence, origin.x, origin.y,
            Mathf.Clamp(velocity.x, -1000f, 1000f), Mathf.Clamp(velocity.y, -1000f, 1000f), body.isRight, body.standing, limbs);
    }

    internal bool Receive(CSteamID sender, byte[] bytes)
    {
        RoomFrame frame = (RoomFrame)bytes[0];
        if (lobby.IsHost && returningPeers.Contains(sender) &&
            (frame != RoomFrame.ItemSyncRequest || !recoveryReceipts.ContainsKey(sender)))
        {
            return true;
        }

        return frame switch
        {
            RoomFrame.SandboxPosition => ReceivePose(sender, bytes),
            RoomFrame.SandboxSound => ReceiveSound(sender, bytes),
            RoomFrame.FluidChunk or RoomFrame.FluidPush or RoomFrame.FluidDrinkRequest or RoomFrame.FluidDrinkResult =>
                !Ready || (WaitingForRecovery && frame == RoomFrame.FluidPush) || Fluids.Receive(sender, bytes),
            RoomFrame.SandboxLights => lights.Receive(sender, bytes),
            _ => Items.Receive(sender, bytes)
        };
    }

    private bool ReceivePose(CSteamID sender, byte[] bytes)
    {
        if (!SandboxWire.TryReadPosition(bytes, plan.Id, out SandboxPosition pose))
        {
            return true;
        }

        if (!IsExpectedSource(sender, pose.Player))
        {
            return false;
        }

        if (!remotes.TryGetValue(pose.Player, out RemotePlayer remote))
        {
            remote = new RemotePlayer();
            remotes.Add(pose.Player, remote);
        }
        else if (unchecked((int)(pose.Sequence - remote.Pose.Sequence)) <= 0)
        {
            return true;
        }

        remote.Pose = pose;
        remote.UpdatedAt = Time.unscaledTime;
        if (lobby.IsHost)
        {
            Broadcast(bytes, sender);
        }
        return true;
    }

    internal bool TryGetPosition(ulong player, out Vector2 position)
    {
        position = default;
        if (player == lobby.LocalUser.m_SteamID && PlayerCamera.main && PlayerCamera.main.body)
        {
            position = PlayerCamera.main.body.transform.position;
            return Ready;
        }

        if (lobby.IsHost && returningPeers.Contains(new CSteamID(player)))
        {
            if (recoveryReceipts.TryGetValue(new CSteamID(player), out var receipt) && receipt.State.HasPosition)
            {
                position = new Vector2(receipt.State.X, receipt.State.Y);
                return true;
            }
            return false;
        }

        if (remotes.TryGetValue(player, out RemotePlayer remote) && Time.unscaledTime - remote.UpdatedAt <= PoseTimeout)
        {
            position = new Vector2(remote.Pose.X, remote.Pose.Y);
            return true;
        }
        return false;
    }

    internal void CaptureSound(string? key, AudioClip clip, Vector2 origin, AudioSource playback)
    {
        if (!Ready || WaitingForRecovery || !PlayerCamera.main || !PlayerCamera.main.body)
        {
            return;
        }

        Body body = PlayerCamera.main.body;
        string clipKey = key ?? audio.GetClipKey(clip, body);
        var sound = new SandboxSound(lobby.LocalUser.m_SteamID, ++soundSequence, origin.x, origin.y,
            Mathf.Clamp01(playback.volume), Mathf.Clamp(playback.pitch, 0.05f, 3f), clipKey);
        if (SandboxWire.WriteSound(plan.Id, sound) is { } frame)
        {
            SendLocal(frame);
        }
    }

    private bool ReceiveSound(CSteamID sender, byte[] bytes)
    {
        if (!SandboxWire.TryReadSound(bytes, plan.Id, out SandboxSound sound))
        {
            return true;
        }

        if (!IsExpectedSource(sender, sound.Player))
        {
            return false;
        }

        if (!Ready || !remotes.TryGetValue(sound.Player, out RemotePlayer remote) ||
            unchecked((int)(sound.Sequence - remote.SoundSequence)) <= 0 ||
            !TryGetPosition(sound.Player, out Vector2 emitter) ||
            (emitter - new Vector2(sound.X, sound.Y)).sqrMagnitude > 24f * 24f)
        {
            return true;
        }

        remote.SoundSequence = sound.Sequence;
        if (Time.unscaledTime - remote.SoundWindow >= 1f)
        {
            remote.SoundWindow = Time.unscaledTime;
            remote.SoundCount = 0;
        }
        if (++remote.SoundCount > SoundsPerSecond)
        {
            return true;
        }

        if (PlayerCamera.main && PlayerCamera.main.body && plan.TryGetPlayer(sound.Player, out LaunchPlayer member))
        {
            audio.Play(sound, member.Character, PlayerCamera.main.body.transform.position);
        }
        if (lobby.IsHost)
        {
            Broadcast(bytes, sender);
        }
        return true;
    }

    private bool IsExpectedSource(CSteamID sender, ulong player) =>
        (lobby.IsHost ? player == sender.m_SteamID : sender == lobby.Owner) &&
        player != lobby.LocalUser.m_SteamID && plan.TryGetPlayer(player, out _);

    private void SendLocal(byte[] frame)
    {
        if (lobby.IsHost)
        {
            Broadcast(frame, lobby.LocalUser);
        }
        else if (!WaitingForRecovery)
        {
            lobby.Link.Send(lobby.Owner, frame, reliable: false);
        }
    }

    private void Broadcast(byte[] frame, CSteamID except)
    {
        foreach (LaunchPlayer player in plan.Players)
        {
            CSteamID id = new(player.SteamId);
            if (id != lobby.LocalUser && id != except)
            {
                lobby.Link.Send(id, frame, reliable: false);
            }
        }
    }

    public void Dispose()
    {
        Ready = false;
        lobby.Link.PeerChanged -= OnRecoveryPeerChanged;
        Items.Dispose();
        Fluids.Dispose();
        lights.Dispose();
        audio.Dispose();
        foreach (RemotePlayer remote in remotes.Values)
        {
            remote.View?.Dispose();
        }
    }
}
