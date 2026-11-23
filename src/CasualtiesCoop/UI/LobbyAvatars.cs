using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CasualtiesCoop.UI;

internal sealed class LobbyAvatars : IDisposable
{
    private readonly Dictionary<int, Texture2D> avatars = new();
    internal Texture2D? Get(CSteamID player)
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
