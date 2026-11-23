using System;
using System.Collections.Generic;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using Steamworks;
using TMPro;
using UnityEngine;

namespace CasualtiesCoop.Session;

internal sealed class SandboxPresence : IDisposable
{
    private const float UpdateInterval = 0.067f;
    private const float MarkerTimeout = 2f;

    private sealed class RemotePlayer
    {
        internal uint Sequence;
        internal Vector2 Target;
        internal float UpdatedAt;
        internal TextMeshPro? Marker;
    }

    private readonly SteamLobby lobby;
    private readonly RoomLaunchPlan plan;
    private readonly TMP_FontAsset font;
    private readonly Dictionary<ulong, RemotePlayer> remotes = new();
    private uint sequence;
    private float nextUpdate;

    internal SandboxPresence(SteamLobby lobby, RoomLaunchPlan plan, TMP_FontAsset font)
    {
        this.lobby = lobby;
        this.plan = plan;
        this.font = font;
    }

    internal bool IsParticipant(CSteamID player, Guid launchId) =>
        launchId == plan.Id && plan.TryGetPlayer(player.m_SteamID, out _);

    internal void Tick()
    {
        if (!PlayerCamera.main || !PlayerCamera.main.body)
        {
            return;
        }

        float now = Time.unscaledTime;
        if (now >= nextUpdate)
        {
            nextUpdate = now + UpdateInterval;
            Vector2 position = PlayerCamera.main.body.transform.position;
            var update = new SandboxPosition(lobby.LocalUser.m_SteamID, ++sequence, position.x, position.y);
            byte[] frame = SandboxWire.WritePosition(plan.Id, update);
            if (lobby.IsHost)
            {
                Broadcast(frame, lobby.LocalUser);
            }
            else
            {
                lobby.Link.Send(lobby.Owner, frame, reliable: false);
            }
        }

        foreach (KeyValuePair<ulong, RemotePlayer> entry in remotes)
        {
            RemotePlayer remote = entry.Value;
            if (now - remote.UpdatedAt > MarkerTimeout)
            {
                if (remote.Marker)
                {
                    remote.Marker!.gameObject.SetActive(false);
                }
                continue;
            }

            Vector3 target = new(remote.Target.x, remote.Target.y + 2.4f, -0.1f);
            if (!remote.Marker)
            {
                remote.Marker = CreateMarker(new CSteamID(entry.Key), target);
            }

            TextMeshPro marker = remote.Marker!;
            if (!marker.gameObject.activeSelf || (marker.transform.position - target).sqrMagnitude > 64f)
            {
                marker.transform.position = target;
                marker.gameObject.SetActive(true);
            }
            else
            {
                marker.transform.position = Vector3.Lerp(marker.transform.position, target, Time.unscaledDeltaTime * 15f);
            }
        }
    }

    internal bool Receive(CSteamID sender, byte[] bytes)
    {
        if (!SandboxWire.TryReadPosition(bytes, plan.Id, out SandboxPosition position))
        {
            return true; // Out-of-order position reports can outlive a scene transition.
        }

        bool fromExpectedPeer = lobby.IsHost ? position.Player == sender.m_SteamID : sender == lobby.Owner;
        if (!fromExpectedPeer || position.Player == lobby.LocalUser.m_SteamID || !plan.TryGetPlayer(position.Player, out _))
        {
            return false;
        }

        if (!remotes.TryGetValue(position.Player, out RemotePlayer remote))
        {
            remote = new RemotePlayer();
            remotes.Add(position.Player, remote);
        }
        else if (unchecked((int)(position.Sequence - remote.Sequence)) <= 0)
        {
            return true;
        }

        remote.Sequence = position.Sequence;
        remote.Target = new Vector2(position.X, position.Y);
        remote.UpdatedAt = Time.unscaledTime;
        if (lobby.IsHost)
        {
            Broadcast(bytes, sender);
        }

        return true;
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

    private TextMeshPro CreateMarker(CSteamID player, Vector3 at)
    {
        GameObject marker = new("Co-op sandbox player marker");
        marker.transform.position = at;
        TextMeshPro text = marker.AddComponent<TextMeshPro>();
        text.font = font;
        text.text = SteamFriends.GetFriendPersonaName(player);
        text.richText = false;
        text.fontSize = 5;
        text.alignment = TextAlignmentOptions.Center;
        text.color = new Color32(143, 216, 182, 255);
        text.rectTransform.sizeDelta = new Vector2(14, 2);
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.sortingOrder = 2000;
        return text;
    }

    public void Dispose()
    {
        foreach (RemotePlayer remote in remotes.Values)
        {
            if (remote.Marker)
            {
                UnityEngine.Object.Destroy(remote.Marker!.gameObject);
            }
        }
    }
}
