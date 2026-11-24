using System;
using System.Buffers.Binary;
using CasualtiesCoop.Gameplay.Audio;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Gameplay.Characters;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;

VerifyHandshake();
VerifyLaunchPlan();
VerifyCharacterPose();
VerifyCharacterSound();
WireCompatibilityChecks.Run();
VerifySandboxGameplay();
WorldChunkProtocolChecks.Run();
Console.WriteLine("Handshake, launch plan, character, item, fluid and rejoin wire checks passed.");

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

    LimbPose[] limbs = new LimbPose[CharacterPoseWire.MaxLimbs];
    limbs[^1] = new LimbPose(-2, 3, 348, 1, 1, true);
    AnimatorLayerPose[] body = new AnimatorLayerPose[CharacterPoseWire.MaxLayers];
    AnimatorLayerPose[] arms = new AnimatorLayerPose[CharacterPoseWire.MaxLayers];
    body[0] = new AnimatorLayerPose(unchecked((int)0xABCD1234), 3.25f, 25, 0.125f, 0.5f);
    arms[^1] = new AnimatorLayerPose(21, 1.25f);
    FacePose face = new(4, 15, 123);
    frame = CharacterPoseWire.Write(session, new CharacterPose(9, 13, 2.5f, -106f,
        facingRight: false, standing: false, limbs: limbs, bodyLayers: body, armLayers: arms, face: face));
    Check(frame.Length <= RoomWire.MaxFrameSize, "Largest character packet fits the room limit");
    Check(CharacterPoseWire.TryRead(frame, session, out pose) && pose.Limbs[^1].Visible && pose.Limbs[^1].Angle == 348 &&
        !pose.Standing && !pose.FacingRight && pose.BodyLayers.Length == body.Length && pose.ArmLayers.Length == arms.Length &&
        pose.BodyLayers[0].State == body[0].State && pose.BodyLayers[0].Phase == 3.25f && pose.BodyLayers[0].NextState == 25 &&
        pose.BodyLayers[0].NextPhase == 0.125f && pose.BodyLayers[0].Transition == 0.5f && pose.ArmLayers[^1].State == 21 &&
        pose.Face.HasExpression && pose.Face.Head == 4 && pose.Face.Eyes == 15 && pose.Face.NoseAlpha == 123, "Animated pose round trip");
    for (int length = 0; length < frame.Length; length++)
    {
        Check(!CharacterPoseWire.TryRead(frame.AsSpan(0, length), session, out _), "Truncated animated pose");
    }

    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(frame.Length - sizeof(float)), BitConverter.SingleToInt32Bits(float.NaN));
    Check(!CharacterPoseWire.TryRead(frame, session, out _), "Invalid animation transition");
}

static void VerifyCharacterSound()
{
    Guid session = Guid.NewGuid();
    var sound = new CharacterSound(9, 14, 3, 5, 0.75f, 1, "dogshake", followPlayer: true);
    byte[] frame = CharacterSoundWire.Write(session, sound) ?? throw new Exception("Could not encode a character sound.");
    Check(CharacterSoundWire.TryRead(frame, session, out CharacterSound copy) && copy.Clip == "dogshake" &&
        copy.FollowPlayer && copy.Sequence == 14 && copy.Volume == 0.75f && copy.X == 3, "Following sound round trip");
    frame[45] = 2;
    Check(!CharacterSoundWire.TryRead(frame, session, out _), "Unknown sound attachment flag");
}

static void VerifySandboxGameplay()
{
    Guid run = new("9c4d229c-0901-4e78-9eed-32498ef6f4f0");
    byte[] hit = SandboxGameplayWire.WriteTerrainHit(run, 120, 92, 7, 4, 20, 1);
    Check(SandboxGameplayWire.TryReadTerrainHit(hit, run, out var tile) &&
        tile.X == 120 && tile.Y == 92 && tile.Block == 7 && tile.Sequence == 4 && tile.Damage == 20, "Terrain hit frame");
    Check(!SandboxGameplayWire.TryReadTerrainHit(hit, Guid.Empty, out _), "Terrain rejects another session");
    hit[31] = 4;
    Check(!SandboxGameplayWire.TryReadTerrainHit(hit, run, out _), "Reject malformed terrain hit flags");
    byte[] claw = SandboxGameplayWire.WriteClaw(run, 3, 4, -10, 2, 145, false);
    Check(SandboxGameplayWire.TryReadClaw(claw, run, out var effect) &&
        effect.Player == 3 && effect.Angle == 145 && !effect.Right, "Claw visual frame");
    claw[41] = 2;
    Check(!SandboxGameplayWire.TryReadClaw(claw, run, out _), "Claw rejects invalid facing");
    byte[] bottle = SandboxGameplayWire.WriteAlcohol(run, 7, -5, -106, 3);
    Check(SandboxGameplayWire.TryReadAlcohol(bottle, run, out var spawn) &&
        spawn.Operation == 7 && spawn.Y == -106 && spawn.Slot == 3, "Sandbox alcohol request");
    Check(!SandboxGameplayWire.TryReadAlcohol(bottle.AsSpan(0, 29), run, out _), "Alcohol rejects truncation");
}

static void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new Exception(name);
    }
}
