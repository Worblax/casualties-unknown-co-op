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
using CasualtiesCoop.Runtime;
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
    private readonly CharacterSpeechReplication speech;
    private readonly LightReplication lights;
    private uint soundSequence;
    private Vector2 initialSpawn;

    internal int PoseFrames { get; private set; }
    internal int PoseInbound { get; private set; }
    internal int PoseInvalid { get; private set; }
    internal int PoseSourceRejected { get; private set; }
    internal int PoseStale { get; private set; }
    internal int PoseAccepted { get; private set; }
    internal int RemoteCount => remotes.Count;

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
        speech = new CharacterSpeechReplication(peers, clock);
        lights = new LightReplication(peers, TryGetPosition, SendLocal, Broadcast, clock,
            Items.TryGetId, FindHeldItem, IsHeldLightCurrent);
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
        if (!WaitingForRecovery && poseSampler.TrySample(body, Items, peers.LocalPlayer, now, out CharacterPose localPose))
        {
            SendLocal(CharacterPoseWire.Write(plan.Id, localPose));
        }

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
                string displayName = CoopRuntime.IsTestMode
                    ? "Player " + entry.Key : SteamFriends.GetFriendPersonaName(new CSteamID(entry.Key));
                remote.View = new RemoteCharacterView(member.Character, displayName, font);
                Items.AttachRegisteredRemoteItems(peers.Identify(entry.Key));
            }
            if (remote.View is { } view)
            {
                if (!WaitingForRecovery && speech.TryTakeLatest(entry.Key, out CharacterSpeech line)) view.PresentSpeech(line);
                view.Apply(remote.Pose, remote.PreviousPose, age, remote.PoseInterval);
            }
        }

        lights.Tick(body);
        audio.Tick(position);
    }

    private Item? FindHeldItem(ulong owner, ulong id) =>
        remotes.TryGetValue(owner, out RemotePlayer remote) && remote.View is { Visible: true } view &&
        view.TryGetHeldItem(id, out Item? item) ? item : null;

    private bool IsHeldLightCurrent(ulong owner, ulong id) =>
        !Items.TryGetById(id, out var state) ||
        (state.Placement == ItemPlacement.Inventory && state.Owner == peers.Identify(owner));

    internal bool TryGetHeldBeam(ulong owner, ulong id, byte component,
        out UnityEngine.Rendering.Universal.Light2D? beam, out bool markerVisible) =>
        lights.TryGetHeldBeam(owner, id, component, out beam, out markerVisible);

    internal bool Receive(ulong sender, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        MessageKind frame = (MessageKind)bytes[0];
        if (frame == MessageKind.CharacterPose) PoseInbound++;
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
            MessageKind.CharacterSpeech => !SceneReady || WaitingForRecovery || speech.Receive(sender, bytes),
            MessageKind.ClawEffect => !SceneReady || Claws.Receive(sender, bytes),
            MessageKind.SandboxAlcohol => !SceneReady || Items.ReceiveAlcohol(sender, bytes),

            MessageKind.FluidPush or MessageKind.FluidDrinkRequest or MessageKind.FluidDrinkResult =>
                !SceneReady || WaitingForRecovery ||
                Fluids.Receive(sender, bytes),
            MessageKind.LightSnapshot => lights.Receive(sender, bytes),
            _ => Items.Receive(sender, bytes)
        };
    }

    private bool ReceivePose(ulong sender, byte[] bytes)
    {
        PoseFrames++;
        if (!CharacterPoseWire.TryRead(bytes, plan.Id, out CharacterPose pose))
        {
            PoseInvalid++;
            CoopDiagnostics.Count("Character", "Invalid pose");
            return true;
        }

        if (!IsExpectedSource(sender, pose.Player))
        {
            PoseSourceRejected++;
            return false;
        }

        if (!remotes.TryGetValue(pose.Player, out RemotePlayer remote))
        {
            remote = new RemotePlayer();
            remotes.Add(pose.Player, remote);
        }
        else if (unchecked((int)(pose.Sequence - remote.Pose.Sequence)) <= 0)
        {
            PoseStale++;
            CoopDiagnostics.Count("Character", "Stale pose");
            return true;
        }

        float now = clock.Now;
        float interval = now - remote.UpdatedAt;
        remote.PreviousPose = interval <= 0.2f ? remote.Pose : default;
        remote.PoseInterval = Mathf.Clamp(interval, CharacterPoseSampler.SendInterval * 0.5f, 0.08f);
        remote.Pose = pose;
        remote.UpdatedAt = now;
        PoseAccepted++;
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

    internal bool TryGetRemotePose(ulong player, out CharacterPose pose)
    {
        if (remotes.TryGetValue(player, out RemotePlayer? remote) && remote.Pose.Player != 0)
        {
            pose = remote.Pose;
            return true;
        }
        pose = default;
        return false;
    }

    internal RemoteCharacterView? RemoteViewFor(ulong player) =>
        remotes.TryGetValue(player, out RemotePlayer? remote) ? remote.View : null;

    internal bool IsRemotePeerVerified(ulong player) => peers.IsVerified(player);

    internal void CaptureSpeech(string text, float timeMultiplier)
    {
        if (SceneReady && !WaitingForRecovery) speech.Send(text, timeMultiplier);
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

    bool IRemoteItemVisuals.AttachHeldItem(ParticipantId owner, ulong id, byte slot, Item item)
    {
        ulong address = peers.AddressOf(owner);
        return address != 0 && remotes.TryGetValue(address, out RemotePlayer? remote) &&
            remote.View is not null && remote.View.AttachHeldItem(id, slot, item);
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
