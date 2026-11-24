using System;

namespace CasualtiesCoop.Lobby;

[Flags]
internal enum RoomOptions
{
    None = 0,
    PvP = 1,
    Respawn = 2,
    KeepItems = 4,
    KeepCheckpoint = 8,
    Sandbox = 16,
    Defaults = Respawn | KeepItems | KeepCheckpoint,
    All = PvP | Defaults | Sandbox
}

internal static class RoomRules
{
    internal const int Capacity = 4;
}
