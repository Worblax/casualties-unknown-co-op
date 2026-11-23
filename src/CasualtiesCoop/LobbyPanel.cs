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
    private Vector2 scroll;

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

        bool wasEnabled = GUI.enabled;
        GUILayout.BeginArea(new Rect(Screen.width - 344, 66, 320, Mathf.Min(460, Screen.height - 72)), GUI.skin.box);
        scroll = GUILayout.BeginScrollView(scroll);
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
            GUI.enabled = wasEnabled;
            GUILayout.EndScrollView();
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
            string status = lobby.TryGetReadyCharacter(player, out int character)
                ? $"Ready: {Locale.GetOther($"char{character}name")}"
                : "Waiting";
            GUILayout.Label(status);
            GUILayout.EndHorizontal();
        }

        GUILayout.Label($"Difficulty: {lobby.Difficulty}");
        if (lobby.IsHost && GUILayout.Button("Next difficulty"))
        {
            int index = RunSettings.presets.FindIndex(preset => preset.presetName == lobby.Difficulty);
            int next = (index + 1) % RunSettings.presets.Count;
            lobby.ChangeDifficulty(RunSettings.presets[next].presetName);
        }

        GUILayout.Label("Your character");
        for (int character = 0; character < SteamLobby.CharacterCount; character++)
        {
            bool unlocked = SteamLobby.IsCharacterUnlocked(character);
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && unlocked && character != lobby.SelectedCharacter;
            string name = Locale.GetOther($"char{character}name");
            string suffix = unlocked ? (character == lobby.SelectedCharacter ? " (selected)" : "") : " (locked)";
            if (GUILayout.Button(name + suffix))
            {
                lobby.SelectCharacter(character);
            }
            GUI.enabled = wasEnabled;
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
