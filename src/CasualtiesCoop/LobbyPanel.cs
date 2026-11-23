using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CasualtiesCoop;

internal sealed class LobbyPanel : IDisposable
{
    private readonly SteamLobby lobby;
    private readonly Dictionary<int, Texture2D> avatars = new();
    private bool open;

    internal LobbyPanel(SteamLobby lobby)
    {
        this.lobby = lobby;
    }

    internal void Draw()
    {
        if (GUI.Button(new Rect(Screen.width - 160, 24, 136, 34), "Co-op"))
        {
            open = !open;
        }

        if (!open && !lobby.InRoom && !lobby.IsBusy && lobby.Status is null)
        {
            return;
        }

        GUILayout.BeginArea(new Rect(Screen.width - 344, 66, 320, 310), GUI.skin.box);
        try
        {
            if (lobby.InRoom)
            {
                DrawRoom();
            }
            else
            {
                GUI.enabled = !lobby.IsBusy;
                if (GUILayout.Button("Create Steam room"))
                {
                    lobby.Create();
                }
            }

            if (lobby.Status is not null)
            {
                GUILayout.Label(lobby.Status);
            }
        }
        finally
        {
            GUI.enabled = true;
            GUILayout.EndArea();
        }
    }

    private void DrawRoom()
    {
        CSteamID room = lobby.Current;
        int count = SteamMatchmaking.GetNumLobbyMembers(room);
        CSteamID owner = SteamMatchmaking.GetLobbyOwner(room);
        GUILayout.Label($"Steam room   {count}/{SteamLobby.Capacity}");

        for (int index = 0; index < count; index++)
        {
            CSteamID player = SteamMatchmaking.GetLobbyMemberByIndex(room, index);
            GUILayout.BeginHorizontal();
            GUILayout.Label(GetAvatar(player), GUILayout.Width(28), GUILayout.Height(28));
            string suffix = player == owner ? " (host)" : "";
            GUILayout.Label(SteamFriends.GetFriendPersonaName(player) + suffix);
            GUILayout.FlexibleSpace();
            GUILayout.Label(lobby.IsReady(player) ? "Ready" : "Waiting");
            GUILayout.EndHorizontal();
        }

        if (lobby.IsHost && GUILayout.Button("Invite friends"))
        {
            lobby.Invite();
        }

        if (GUILayout.Button(lobby.IsReady(lobby.LocalUser) ? "Cancel ready" : "Ready"))
        {
            lobby.ToggleReady();
        }

        if (GUILayout.Button("Leave room"))
        {
            lobby.Leave();
            open = true;
        }
    }

    private Texture2D? GetAvatar(CSteamID player)
    {
        int image = SteamFriends.GetSmallFriendAvatar(player);
        if (image <= 0)
        {
            return null;
        }

        if (avatars.TryGetValue(image, out Texture2D? cached))
        {
            return cached;
        }

        if (!SteamUtils.GetImageSize(image, out uint width, out uint height) || width == 0 || height == 0)
        {
            return null;
        }

        byte[] pixels = new byte[checked((int)(width * height * 4))];
        if (!SteamUtils.GetImageRGBA(image, pixels, pixels.Length))
        {
            return null;
        }

        int rowBytes = checked((int)width * 4);
        byte[] row = new byte[rowBytes];
        for (int y = 0; y < height / 2; y++)
        {
            int top = y * rowBytes;
            int bottom = checked(((int)height - y - 1) * rowBytes);
            Buffer.BlockCopy(pixels, top, row, 0, rowBytes);
            Buffer.BlockCopy(pixels, bottom, pixels, top, rowBytes);
            Buffer.BlockCopy(row, 0, pixels, bottom, rowBytes);
        }

        Texture2D texture = new((int)width, (int)height, TextureFormat.RGBA32, false);
        texture.LoadRawTextureData(pixels);
        texture.Apply(false, true);
        avatars.Add(image, texture);
        return texture;
    }

    public void Dispose()
    {
        foreach (Texture2D texture in avatars.Values)
        {
            Object.Destroy(texture);
        }

        avatars.Clear();
    }
}
