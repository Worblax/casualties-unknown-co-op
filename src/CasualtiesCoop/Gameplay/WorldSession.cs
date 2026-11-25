using System;
using System.Collections.Generic;
using BepInEx.Logging;
using CasualtiesCoop.Core;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Diagnostics;
using CasualtiesCoop.Gameplay.Audio;
using CasualtiesCoop.Gameplay.Characters;
using CasualtiesCoop.Gameplay.Fluids;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Gameplay.Lighting;
using CasualtiesCoop.Gameplay.Terrain;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;
using Steamworks;
using TMPro;
using UnityEngine;

namespace CasualtiesCoop.Gameplay;

internal delegate bool PlayerPositionLookup(ulong player, out Vector2 position);

internal sealed partial class WorldSession : IDisposable, IRemoteItemVisuals
{
    private const float PoseTimeout = 2f;
    private const int SoundsPerSecond = 48;

    private sealed class RemotePlayer
    {
        internal CharacterPose Pose;
        internal CharacterPose PreviousPose;
        internal float PoseInterval;
        internal float UpdatedAt;
        internal uint SoundSequence;
        internal float SoundWindow;
        internal int SoundCount;
        internal RemoteCharacterView? View;
    }

    private readonly SessionPeers peers;
    private readonly GuestRecovery guestRecovery;
    private readonly RoomLaunchPlan plan;
    private readonly TMP_FontAsset font;
    private readonly ManualLogSource log;
    private readonly IMonotonicClock clock;
    private readonly Dictionary<ulong, RemotePlayer> remotes = new();
    private readonly SpatialCharacterAudio audio;
    private readonly CharacterPoseSampler poseSampler = new();
    private readonly LightReplication lights;
    private uint soundSequence;
    private Vector2 initialSpawn;

    internal bool SceneReady { get; private set; }
    internal SharedItemReplication Items { get; }
    internal TerrainFluidReplication Fluids { get; }
    internal ClawEffects Claws { get; }
    internal TerrainDamageReplication Terrain { get; }
    internal WorldChunkReplication WorldChunks { get; }
    internal bool WaitingForWorld => WaitingForRecovery || (SceneReady && !WorldChunks.Ready);

    internal WorldSession(SessionPeers peers, RoomLaunchPlan plan, TMP_FontAsset font, ManualLogSource log, IMonotonicClock clock)
    {
        this.peers = peers;
        this.plan = plan;
        this.font = font;
        this.log = log;
        this.clock = clock;
        guestRecovery = new GuestRecovery(clock);
        audio = new SpatialCharacterAudio(TryGetPosition);
        Items = new SharedItemReplication(peers, TryGetPosition, clock, this);
        Fluids = new TerrainFluidReplication(peers, TryGetPosition, clock);
        Claws = new ClawEffects(peers, clock, TryGetPosition);
        WorldChunks = new WorldChunkReplication(peers, clock, TryGetPosition);
        Terrain = new TerrainDamageReplication(peers, clock, TryGetPosition, WorldChunks);
        lights = new LightReplication(peers, TryGetPosition, SendLocal, Broadcast, clock);
        peers.ConnectionChanged += OnRecoveryPeerChanged;
        peers.AttachReceiver(Receive);
    }

    internal bool IsParticipant(ulong player, Guid launchId) =>
        launchId == plan.Id && plan.TryGetPlayer(player, out _);

    internal void Tick()
    {
        if (!PlayerCamera.main || !PlayerCamera.main.body)
        {
            return;
        }

        Body body = PlayerCamera.main.body;
        if (!SceneReady) initialSpawn = body.transform.position;
        SceneReady = true;
        TickRecovery(body);
        Items.Tick(body);
        Fluids.Tick();
        WorldChunks.Tick();
    }

    internal void TickPresentation()
    {
        if (!SceneReady || !PlayerCamera.main || !PlayerCamera.main.body)
        {
            return;
        }

        Body body = PlayerCamera.main.body;
        float now = clock.Now;
        Vector2 position = body.transform.position;
        if (!WaitingForRecovery && poseSampler.TrySample(body, peers.LocalPlayer, now, out CharacterPose localPose))
        {
            SendLocal(CharacterPoseWire.Write(plan.Id, localPose));
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
                remote.View = new RemoteCharacterView(member.Character, SteamFriends.GetFriendPersonaName(new CSteamID(entry.Key)), font);
            }
            remote.View?.Apply(remote.Pose, remote.PreviousPose, age, remote.PoseInterval);
        }

        audio.Tick(position);
    }

    internal bool Receive(ulong sender, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        MessageKind frame = (MessageKind)bytes[0];
        if (frame is >= MessageKind.SessionRejoinRequest and <= MessageKind.SessionRejoinCommitted)
        {
            return ReceiveRecovery(sender, bytes);
        }
        if (peers.IsHost && !hostRecovery.AllowsGameplay(sender, frame))
        {
            return true;
        }

        if (WorldChunkReplication.Handles(frame))
        {
            if (!SceneReady || (!peers.IsHost && WaitingForRecovery && guestRecovery.Snapshot is null)) return true;
            return WorldChunks.Receive(sender, bytes, peers.IsHost && hostRecovery.IsReturning(sender)
                ? hostRecovery.GetOffer(sender)?.Attempt ?? Guid.Empty : Guid.Empty);
        }
        if (frame == MessageKind.TerrainHit) return Terrain.Receive(sender, bytes);

        return frame switch
        {
            MessageKind.CharacterPose => ReceivePose(sender, bytes),
            MessageKind.CharacterSound => ReceiveSound(sender, bytes),
            MessageKind.ClawEffect => !SceneReady || Claws.Receive(sender, bytes),
            MessageKind.SandboxAlcohol => !SceneReady || Items.ReceiveAlcohol(sender, bytes),

            MessageKind.FluidPush or MessageKind.FluidDrinkRequest or MessageKind.FluidDrinkResult =>
                !SceneReady || WaitingForRecovery ||
                Fluids.Receive(sender, bytes),
            MessageKind.LightSnapshot => lights.Receive(sender, bytes),
            MessageKind.ItemRequest or MessageKind.ItemState or MessageKind.ItemResult or MessageKind.ItemMotion or
                MessageKind.ItemSyncRequest or MessageKind.ItemSyncDone or MessageKind.ItemDelta or MessageKind.ItemSnapshotEntry =>
                Items.Receive(sender, bytes),
            _ => false
        };
    }

    private bool ReceivePose(ulong sender, byte[] bytes)
    {
        if (!CharacterPoseWire.TryRead(bytes, plan.Id, out CharacterPose pose))
        {
            CoopDiagnostics.Count("Character", "Invalid pose");
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
            CoopDiagnostics.Count("Character", "Stale pose");
            return true;
        }

        float now = clock.Now;
        float interval = now - remote.UpdatedAt;
        remote.PreviousPose = interval <= 0.2f ? remote.Pose : default;
        remote.PoseInterval = Mathf.Clamp(interval, CharacterPoseSampler.SendInterval * 0.5f, 0.08f);
        remote.Pose = pose;
        remote.UpdatedAt = now;
        if (peers.IsHost)
        {
            Broadcast(bytes, sender);
        }
        return true;
    }

    internal bool TryGetPosition(ulong player, out Vector2 position)
    {
        position = default;
        if (player == peers.LocalPlayer && PlayerCamera.main && PlayerCamera.main.body)
        {
            position = PlayerCamera.main.body.transform.position;
            return SceneReady;
        }

        if (peers.IsHost && hostRecovery.IsReturning(player))
        {
            if (hostRecovery.GetOffer(player) is { HasPosition: true } snapshot)
            {
                position = new Vector2(snapshot.X, snapshot.Y);
                return true;
            }
            return false;
        }

        if (remotes.TryGetValue(player, out RemotePlayer remote) && clock.Now - remote.UpdatedAt <= PoseTimeout)
        {
            position = new Vector2(remote.Pose.X, remote.Pose.Y);
            return true;
        }
        return false;
    }

    internal void CaptureSound(string? key, AudioClip clip, Vector2 origin, AudioSource playback, bool followPlayer)
    {
        if (!SceneReady || WaitingForRecovery || !PlayerCamera.main || !PlayerCamera.main.body)
        {
            return;
        }

        Body body = PlayerCamera.main.body;
        string clipKey = key ?? audio.GetClipKey(clip, body);
        CoopDiagnostics.Count("Audio sent", clipKey);
        var sound = new CharacterSound(peers.LocalPlayer, ++soundSequence, origin.x, origin.y,
            Mathf.Clamp01(playback.volume), Mathf.Clamp(playback.pitch, 0.05f, 3f), clipKey, followPlayer);
        if (CharacterSoundWire.Write(plan.Id, sound) is { } frame)
        {
            SendLocal(frame);
        }
    }

    private bool ReceiveSound(ulong sender, byte[] bytes)
    {
        if (!CharacterSoundWire.TryRead(bytes, plan.Id, out CharacterSound sound))
        {
            CoopDiagnostics.Count("Audio", "Invalid sound frame");
            return true;
        }

        if (!IsExpectedSource(sender, sound.Player))
        {
            return false;
        }

        if (!SceneReady || !remotes.TryGetValue(sound.Player, out RemotePlayer remote) ||
            unchecked((int)(sound.Sequence - remote.SoundSequence)) <= 0 ||
            !TryGetPosition(sound.Player, out Vector2 emitter) ||
            (emitter - new Vector2(sound.X, sound.Y)).sqrMagnitude > 24f * 24f)
        {
            CoopDiagnostics.Count("Audio", "Sound outside current pose window");
            return true;
        }

        remote.SoundSequence = sound.Sequence;
        if (clock.Now - remote.SoundWindow >= 1f)
        {
            remote.SoundWindow = clock.Now;
            remote.SoundCount = 0;
        }
        if (++remote.SoundCount > SoundsPerSecond)
        {
            return true;
        }

        if (PlayerCamera.main && PlayerCamera.main.body && plan.TryGetPlayer(sound.Player, out LaunchPlayer member))
        {
            audio.Play(sound, member.Character, emitter, PlayerCamera.main.body.transform.position);
        }
        if (peers.IsHost)
        {
            Broadcast(bytes, sender);
        }
        return true;
    }

    private bool IsExpectedSource(ulong sender, ulong player) =>
        (peers.IsHost ? player == sender : sender == peers.Host) &&
        player != peers.LocalPlayer && plan.TryGetPlayer(player, out _);

    private void SendLocal(byte[] frame)
    {
        if (peers.IsHost)
        {
            Broadcast(frame, peers.LocalPlayer);
        }
        else if (!WaitingForRecovery)
        {
            peers.Send(peers.Host, frame, reliable: false);
        }
    }

    private void Broadcast(byte[] frame, ulong except)
    {
        foreach (LaunchPlayer player in plan.Players)
        {
            ulong id = player.SteamId;
            if (id != peers.LocalPlayer && id != except)
            {
                peers.Send(id, frame, reliable: false);
            }
        }
    }

    bool IRemoteItemVisuals.AttachHeldItem(ParticipantId owner, byte slot, Item item)
    {
        ulong address = peers.AddressOf(owner);
        return address != 0 && remotes.TryGetValue(address, out RemotePlayer? remote) &&
            remote.View is not null && remote.View.AttachHeldItem(slot, item);
    }

    bool IRemoteItemVisuals.AttachWornItem(ParticipantId owner, Item item)
    {
        ulong address = peers.AddressOf(owner);
        return address != 0 && remotes.TryGetValue(address, out RemotePlayer? remote) &&
            remote.View is not null && remote.View.AttachWornItem(item);
    }

    void IRemoteItemVisuals.DetachHeldItem(Item item)
    {
        foreach (RemotePlayer remote in remotes.Values) remote.View?.DetachHeldItem(item);
    }

    public void Dispose()
    {
        SceneReady = false;
        peers.ConnectionChanged -= OnRecoveryPeerChanged;
        peers.Dispose();
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
