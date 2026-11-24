using System;
using System.Collections.Generic;
using CasualtiesCoop.Lobby;

namespace CasualtiesCoop.Session;

internal readonly struct LaunchPlayer
{
    internal readonly ulong SteamId;
    internal readonly byte Character;

    internal LaunchPlayer(ulong steamId, byte character)
    {
        SteamId = steamId;
        Character = character;
    }
}

internal sealed class RoomLaunchPlan
{
    internal Guid Id { get; }
    internal Guid SettingsRevision { get; }
    internal string Difficulty { get; }
    internal RoomOptions Options { get; }
    internal IReadOnlyList<LaunchPlayer> Players { get; }

    internal RoomLaunchPlan(Guid id, Guid revision, string difficulty, RoomOptions options, LaunchPlayer[] players)
    {
        Id = id;
        SettingsRevision = revision;
        Difficulty = difficulty;
        Options = options;
        Players = Array.AsReadOnly((LaunchPlayer[])players.Clone());
    }

    internal bool TryGetPlayer(ulong steamId, out LaunchPlayer player)
    {
        foreach (LaunchPlayer candidate in Players)
        {
            if (candidate.SteamId == steamId)
            {
                player = candidate;
                return true;
            }
        }

        player = default;
        return false;
    }
}
