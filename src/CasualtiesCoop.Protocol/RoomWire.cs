using System;
using System.Buffers.Binary;
using System.Text;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Session;

namespace CasualtiesCoop.Networking;

internal enum MessageKind : byte
{
    None,
    GuestHello,
    HostWelcome,
    GuestConfirm,
    PrepareRun,
    RunPrepared,
    StartSandbox,
    SandboxRejected,
    CharacterPose,
    CharacterSound,
    ItemRequest,
    ItemState,
    ItemResult,
    ItemMotion,
    ItemSyncRequest,
    ItemSyncDone,
    ReservedFluidChunk,
    FluidPush,
    FluidDrinkRequest,
    FluidDrinkResult,
    LightSnapshot,
    SessionRejoinRequest,
    SessionRejoinState,
    SessionRejoinApplied,
    SessionRejoinCommitted,
    ItemDelta,
    ItemSnapshotEntry,
    ClawEffect,
    TerrainHit,
    ReservedTerrainState,
    ReservedTerrainSync,
    SandboxAlcohol,
    WorldChunkControl,
    WorldChunkSnapshot
}

internal static class RoomWire
{
    internal const string LobbyProtocolVersion = "12";
    internal const int MaxFrameSize = 768;
    private const int HandshakeSize = 41;
    private const int PlanHeaderSize = 36;
    private const int PlayerSize = 9;
    private const int MaxDifficultyBytes = 80;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static byte[] Handshake(MessageKind frame, ulong lobby, Guid gameBuild, Guid modBuild)
    {
        byte[] message = new byte[HandshakeSize];
        message[0] = (byte)frame;
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(1), lobby);
        gameBuild.TryWriteBytes(message.AsSpan(9));
        modBuild.TryWriteBytes(message.AsSpan(25));
        return message;
    }

    internal static bool IsHandshake(ReadOnlySpan<byte> message, MessageKind frame, ulong lobby) =>
        message.Length == HandshakeSize && message[0] == (byte)frame &&
        BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(1)) == lobby;

    internal static bool HasMatchingBuild(ReadOnlySpan<byte> handshake, Guid gameBuild, Guid modBuild) =>
        handshake.Length == HandshakeSize && new Guid(handshake.Slice(9, 16)) == gameBuild &&
        new Guid(handshake.Slice(25, 16)) == modBuild;

    internal static byte[]? WritePlan(RoomLaunchPlan plan)
    {
        byte[] difficulty = Utf8.GetBytes(plan.Difficulty);
        int count = plan.Players.Count;
        if (difficulty.Length is 0 or > MaxDifficultyBytes || count is < 2 or > RoomRules.Capacity)
        {
            return null;
        }

        byte[] message = new byte[PlanHeaderSize + difficulty.Length + count * PlayerSize];
        message[0] = (byte)MessageKind.PrepareRun;
        plan.Id.TryWriteBytes(message.AsSpan(1));
        plan.SettingsRevision.TryWriteBytes(message.AsSpan(17));
        message[33] = (byte)plan.Options;
        message[34] = (byte)difficulty.Length;
        message[35] = (byte)count;
        difficulty.CopyTo(message, PlanHeaderSize);
        int offset = PlanHeaderSize + difficulty.Length;
        foreach (LaunchPlayer player in plan.Players)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(offset), player.SteamId);
            message[offset + 8] = player.Character;
            offset += PlayerSize;
        }

        return message;
    }

    internal static RoomLaunchPlan? ReadPlan(ReadOnlySpan<byte> message)
    {
        if (message.Length < PlanHeaderSize || message[0] != (byte)MessageKind.PrepareRun)
        {
            return null;
        }

        Guid id = new(message.Slice(1, 16));
        Guid revision = new(message.Slice(17, 16));
        RoomOptions options = (RoomOptions)message[33];
        int difficultySize = message[34];
        int count = message[35];
        if (id == Guid.Empty || revision == Guid.Empty || (options & ~RoomOptions.All) != 0 ||
            difficultySize is 0 or > MaxDifficultyBytes || count is < 2 or > RoomRules.Capacity ||
            message.Length != PlanHeaderSize + difficultySize + count * PlayerSize)
        {
            return null;
        }

        string difficulty;
        try
        {
            difficulty = Utf8.GetString(message.Slice(PlanHeaderSize, difficultySize));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        LaunchPlayer[] players = new LaunchPlayer[count];
        int offset = PlanHeaderSize + difficultySize;
        for (int index = 0; index < players.Length; index++)
        {
            ulong player = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(offset));
            if (player == 0 || ContainsPlayer(players, index, player))
            {
                return null;
            }

            players[index] = new LaunchPlayer(player, message[offset + 8]);
            offset += PlayerSize;
        }

        return new RoomLaunchPlan(id, revision, difficulty, options, players);
    }

    internal static byte[] PlanReference(MessageKind frame, Guid id)
    {
        byte[] message = new byte[17];
        message[0] = (byte)frame;
        id.TryWriteBytes(message.AsSpan(1));
        return message;
    }

    internal static Guid ReadPlanReference(MessageKind frame, ReadOnlySpan<byte> message) =>
        message.Length == 17 && message[0] == (byte)frame
            ? new Guid(message.Slice(1))
            : Guid.Empty;

    private static bool ContainsPlayer(LaunchPlayer[] players, int count, ulong steamId)
    {
        for (int index = 0; index < count; index++)
        {
            if (players[index].SteamId == steamId)
            {
                return true;
            }
        }

        return false;
    }
}
