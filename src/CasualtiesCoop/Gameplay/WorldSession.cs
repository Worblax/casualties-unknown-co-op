using System;
using System.Collections.Generic;
using BepInEx.Logging;
using CasualtiesCoop.Gameplay.Audio;
using CasualtiesCoop.Gameplay.Characters;
using CasualtiesCoop.Gameplay.Fluids;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Gameplay.Lighting;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;
using Steamworks;
using TMPro;
using UnityEngine;

namespace CasualtiesCoop.Gameplay;

internal sealed partial class WorldSession : IDisposable
{
    private const float UpdateInterval = 0.067f;
    private const float PoseTimeout = 2f;
    private const int SoundsPerSecond = 48;

    private sealed class RemotePlayer
    {
        internal CharacterPose Pose;
        internal float UpdatedAt;
        internal uint SoundSequence;
        internal float SoundWindow;
        internal int SoundCount;
        internal RemoteCharacterView? View;
    }

    private readonly SessionPeers peers;
    private readonly RoomLaunchPlan plan;
    private readonly TMP_FontAsset font;
    private readonly ManualLogSource log;
    private readonly Dictionary<ulong, RemotePlayer> remotes = new();
    private readonly SpatialCharacterAudio audio = new();
    private readonly LightReplication lights;
    private uint sequence;
    private uint soundSequence;
    private float nextUpdate;

    internal bool Ready { get; private set; }
    internal SharedItemReplication Items { get; }
    internal TerrainFluidReplication Fluids { get; }

    internal WorldSession(SessionPeers peers, RoomLaunchPlan plan, TMP_FontAsset font, ManualLogSource log)
    {
        this.peers = peers;
        this.plan = plan;
        this.font = font;
        this.log = log;
        Items = new SharedItemReplication(peers, TryGetPosition);
        Fluids = new TerrainFluidReplication(peers, TryGetPosition);
        lights = new LightReplication(peers, TryGetPosition, SendLocal, Broadcast);
        peers.ConnectionChanged += OnRecoveryPeerChanged;
    }

    internal bool IsParticipant(ulong player, Guid launchId) =>
        launchId == plan.Id && plan.TryGetPlayer(player, out _);

    internal void Tick()
    {
        if (!PlayerCamera.main || !PlayerCamera.main.body)
        {
            return;
        }

        Ready = true;
        Body body = PlayerCamera.main.body;
        TickRecovery(body);
        Items.Tick(body);
        Fluids.Tick();
    }

    internal void TickPresentation()
    {
        if (!Ready || !PlayerCamera.main || !PlayerCamera.main.body)
        {
            return;
        }

        Body body = PlayerCamera.main.body;
        float now = Time.unscaledTime;
        Vector2 position = body.transform.position;
        if (!WaitingForRecovery && now >= nextUpdate)
        {
            nextUpdate = now + UpdateInterval;
            SendLocal(CharacterPoseWire.Write(plan.Id, CapturePose(body, position)));
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
            remote.View?.Apply(remote.Pose, age);
        }

        audio.Tick(position);
    }

    private CharacterPose CapturePose(Body body, Vector2 origin)
    {
        int count = Math.Min(body.limbs.Length, CharacterPoseWire.MaxLimbs);
        LimbPose[] limbs = new LimbPose[count];
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
                limbs[index] = new LimbPose(offset.x, offset.y, transform.eulerAngles.z,
                    Mathf.Min(8, Mathf.Abs(scale.x)), Mathf.Min(8, Mathf.Abs(scale.y)), limb.gameObject.activeInHierarchy);
            }
        }

        Vector2 velocity = body.standing || count < 3 || !body.limbs[2] ? body.rb.velocity : body.limbs[2].rb.velocity;
        return new CharacterPose(peers.LocalPlayer, ++sequence, origin.x, origin.y,
            Mathf.Clamp(velocity.x, -1000f, 1000f), Mathf.Clamp(velocity.y, -1000f, 1000f), body.isRight, body.standing, limbs);
    }

    internal bool Receive(ulong sender, byte[] bytes)
    {
        MessageKind frame = (MessageKind)bytes[0];
        if (frame is >= MessageKind.SessionRejoinRequest and <= MessageKind.SessionRejoinCommitted)
        {
            return ReceiveRecovery(sender, bytes);
        }
        if (peers.IsHost && returningPeers.Contains(sender) &&
            (frame != MessageKind.ItemSyncRequest || !recoveryReceipts.ContainsKey(sender)))
        {
            return true;
        }

        return frame switch
        {
            MessageKind.CharacterPose => ReceivePose(sender, bytes),
            MessageKind.CharacterSound => ReceiveSound(sender, bytes),
            MessageKind.FluidChunk or MessageKind.FluidPush or MessageKind.FluidDrinkRequest or MessageKind.FluidDrinkResult =>
                !Ready || (WaitingForRecovery && frame == MessageKind.FluidPush) || Fluids.Receive(sender, bytes),
            MessageKind.LightSnapshot => lights.Receive(sender, bytes),
            _ => Items.Receive(sender, bytes)
        };
    }

    private bool ReceivePose(ulong sender, byte[] bytes)
    {
        if (!CharacterPoseWire.TryRead(bytes, plan.Id, out CharacterPose pose))
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
            return Ready;
        }

        if (peers.IsHost && returningPeers.Contains(player))
        {
            if (recoveryReceipts.TryGetValue(player, out var receipt) && receipt.State.HasPosition)
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
        var sound = new CharacterSound(peers.LocalPlayer, ++soundSequence, origin.x, origin.y,
            Mathf.Clamp01(playback.volume), Mathf.Clamp(playback.pitch, 0.05f, 3f), clipKey);
        if (CharacterSoundWire.Write(plan.Id, sound) is { } frame)
        {
            SendLocal(frame);
        }
    }

    private bool ReceiveSound(ulong sender, byte[] bytes)
    {
        if (!CharacterSoundWire.TryRead(bytes, plan.Id, out CharacterSound sound))
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

    public void Dispose()
    {
        Ready = false;
        peers.ConnectionChanged -= OnRecoveryPeerChanged;
        Items.Dispose();
        Fluids.Dispose();
        lights.Dispose();
        audio.Dispose();
        foreach (RemotePlayer remote in remotes.Values)
        {
            remote.View?.Dispose();
        }
        peers.Dispose();
    }
}
