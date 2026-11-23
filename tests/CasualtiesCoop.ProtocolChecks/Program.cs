using System;
using System.Buffers.Binary;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Gameplay.Characters;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;

VerifyHandshake();
VerifyLaunchPlan();
VerifyCharacterPose();
Console.WriteLine("Handshake, launch plan and sandbox packet checks passed.");

static void VerifyHandshake()
{
    const ulong lobby = 109775241238404096;
    Guid game = Guid.NewGuid();
    Guid mod = Guid.NewGuid();
    byte[] hello = RoomWire.Handshake(MessageKind.GuestHello, lobby, game, mod);
    Check(RoomWire.IsHandshake(hello, MessageKind.GuestHello, lobby), "Valid room handshake");
    Check(RoomWire.HasMatchingBuild(hello, game, mod), "Matching game and mod builds");
    Check(!RoomWire.HasMatchingBuild(hello, Guid.NewGuid(), mod), "Different game build");
    Check(!RoomWire.HasMatchingBuild(hello, game, Guid.NewGuid()), "Different mod build");
    Check(!RoomWire.IsHandshake(hello, MessageKind.HostWelcome, lobby), "Wrong handshake stage");
    Check(!RoomWire.IsHandshake(hello, MessageKind.GuestHello, lobby + 1), "Different room");
    Check(!RoomWire.IsHandshake(hello.AsSpan(0, 40), MessageKind.GuestHello, lobby), "Truncated handshake");
}

static void VerifyLaunchPlan()
{
    Guid launchId = Guid.NewGuid();
    Guid settingsRevision = Guid.NewGuid();
    LaunchPlayer[] players = { new(1, 0), new(2, 1), new(3, 0), new(4, 2) };
    RoomOptions options = RoomOptions.Defaults | RoomOptions.Sandbox;
    RoomLaunchPlan plan = new(launchId, settingsRevision, "normal", options, players);
    players[0] = new LaunchPlayer(9, 2);
    Check(plan.TryGetPlayer(1, out LaunchPlayer host) && host.Character == 0, "Frozen roster");

    byte[] frame = RoomWire.WritePlan(plan) ?? throw new Exception("Could not encode the launch plan.");
    RoomLaunchPlan copy = RoomWire.ReadPlan(frame) ?? throw new Exception("Could not decode the launch plan.");
    Check(copy.Id == launchId && copy.SettingsRevision == settingsRevision && copy.Difficulty == "normal" &&
        copy.Options == options && copy.Players.Count == 4 &&
        copy.TryGetPlayer(4, out LaunchPlayer guest) && guest.Character == 2, "Four-player sandbox round trip");

    for (int length = 0; length < frame.Length; length++)
    {
        Check(RoomWire.ReadPlan(frame.AsSpan(0, length)) is null, "Truncated launch plan");
    }

    byte[] invalid = (byte[])frame.Clone();
    invalid[33] = 128;
    Check(RoomWire.ReadPlan(invalid) is null, "Unsupported room flags");
    invalid = (byte[])frame.Clone();
    invalid[36] = 255;
    Check(RoomWire.ReadPlan(invalid) is null, "Malformed difficulty text");
    invalid = (byte[])frame.Clone();
    int firstPlayer = 36 + invalid[34];
    invalid.AsSpan(firstPlayer, 8).CopyTo(invalid.AsSpan(firstPlayer + 9));
    Check(RoomWire.ReadPlan(invalid) is null, "Duplicate Steam ID");

    byte[] prepared = RoomWire.PlanReference(MessageKind.RunPrepared, launchId);
    byte[] start = RoomWire.PlanReference(MessageKind.StartSandbox, launchId);
    byte[] rejected = RoomWire.PlanReference(MessageKind.SandboxRejected, launchId);
    Check(RoomWire.ReadPlanReference(MessageKind.RunPrepared, prepared) == launchId, "Guest acknowledgment");
    Check(RoomWire.ReadPlanReference(MessageKind.StartSandbox, start) == launchId, "Host launch");
    Check(RoomWire.ReadPlanReference(MessageKind.SandboxRejected, rejected) == launchId, "Guest launch rejection");
    Check(RoomWire.ReadPlanReference(MessageKind.StartSandbox, prepared) == Guid.Empty, "Acknowledgment cannot launch a sandbox");
}

static void VerifyCharacterPose()
{
    Guid session = Guid.NewGuid();
    byte[] frame = CharacterPoseWire.Write(session, new CharacterPose(9, 12, 2.5f, -106f));
    Check(CharacterPoseWire.TryRead(frame, session, out CharacterPose pose) &&
        pose.Player == 9 && pose.Sequence == 12 && pose.X == 2.5f && pose.Y == -106f, "Position round trip");
    Check(!CharacterPoseWire.TryRead(frame, Guid.NewGuid(), out _), "Position from another session");
    Check(!CharacterPoseWire.TryRead(frame.AsSpan(0, 36), session, out _), "Truncated position");
    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(29), BitConverter.SingleToInt32Bits(float.NaN));
    Check(!CharacterPoseWire.TryRead(frame, session, out _), "Non-finite coordinate");
    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(29), BitConverter.SingleToInt32Bits(100_000f));
    Check(!CharacterPoseWire.TryRead(frame, session, out _), "Coordinate outside the game world");
}

static void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new Exception(name);
    }
}
