using System;
using System.Buffers.Binary;
using System.IO;
using BepInEx.Logging;
using Steamworks;
using UnityEngine;

namespace CasualtiesCoop.Lobby;

internal readonly struct RoomReturnAddress
{
    internal readonly CSteamID Room;
    internal readonly CSteamID Host;
    internal readonly Guid Session;

    internal RoomReturnAddress(CSteamID room, CSteamID host, Guid session)
    {
        Room = room;
        Host = host;
        Session = session;
    }
}

internal sealed class RoomReturnRecord
{
    private const byte Format = 1;
    private const int Size = 33;
    private readonly string path;
    private readonly ManualLogSource log;

    internal RoomReturnAddress? Address { get; private set; }

    internal RoomReturnRecord(CSteamID account, ManualLogSource log)
    {
        this.log = log;
        path = Path.Combine(Application.persistentDataPath, "CasualtiesCoop", $"return-{account.m_SteamID}.bin");
        try
        {
            using FileStream file = File.OpenRead(path);
            using BinaryReader reader = new(file);
            byte[] bytes = file.Length == Size ? reader.ReadBytes(Size) : Array.Empty<byte>();
            if (bytes.Length == Size && bytes[0] == Format)
            {
                CSteamID room = new(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(1)));
                CSteamID host = new(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(9)));
                Guid session = new(bytes.AsSpan(17));
                if (room.IsLobby() && host.IsValid() && host != account && session != Guid.Empty)
                {
                    Address = new RoomReturnAddress(room, host, session);
                }
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning($"Could not read the co-op return address: {ex.Message}");
        }
    }

    internal void Remember(RoomReturnAddress address)
    {
        Address = address;
        string temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] bytes = new byte[Size];
            bytes[0] = Format;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(1), address.Room.m_SteamID);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(9), address.Host.m_SteamID);
            address.Session.TryWriteBytes(bytes.AsSpan(17));
            using (FileStream file = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes, 0, bytes.Length);
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning($"Could not save the co-op return address: {ex.Message}");
        }
    }

    internal void Forget(CSteamID room)
    {
        if (Address is not { } address || address.Room != room)
        {
            return;
        }

        Address = null;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning($"Could not clear the co-op return address: {ex.Message}");
        }
    }
}
