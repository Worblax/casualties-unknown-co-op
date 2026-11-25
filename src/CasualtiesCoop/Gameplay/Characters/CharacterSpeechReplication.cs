using System.Collections.Generic;
using CasualtiesCoop.Core;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Session;

namespace CasualtiesCoop.Gameplay.Characters;

internal sealed class CharacterSpeechReplication
{
    private const float PendingSeconds = 2f;
    private const int LinesPerSecond = 12;

    private sealed class Speaker
    {
        internal uint Sequence;
        internal float WindowStart;
        internal int Count;
        internal CharacterSpeech? Pending;
        internal float PendingUntil;

        internal bool Admit(float now)
        {
            if (now - WindowStart >= 1f)
            {
                WindowStart = now;
                Count = 0;
            }
            if (Count >= LinesPerSecond) return false;
            Count++;
            return true;
        }
    }

    private readonly SessionPeers peers;
    private readonly IMonotonicClock clock;
    private readonly Dictionary<ulong, Speaker> remote = new();
    private readonly Speaker local = new();

    internal CharacterSpeechReplication(SessionPeers peers, IMonotonicClock clock)
    {
        this.peers = peers;
        this.clock = clock;
    }

    internal void Send(string text, float timeMultiplier)
    {
        uint next = unchecked(local.Sequence + 1);
        var line = new CharacterSpeech(peers.LocalPlayer, next, timeMultiplier, text);
        if (!local.Admit(clock.Now) || CharacterSpeechWire.Write(peers.SessionId, line) is not { } frame)
        {
            return;
        }

        local.Sequence = next;
        if (peers.IsHost) Relay(frame, peers.LocalPlayer);
        else peers.Send(peers.Host, frame, reliable: true);
    }

    internal bool Receive(ulong sender, byte[] frame)
    {
        if (!CharacterSpeechWire.TryRead(frame, peers.SessionId, out CharacterSpeech line)) return true;
        if (line.Player == peers.LocalPlayer || !peers.TryGetPlayer(line.Player, out _) ||
            (peers.IsHost ? line.Player != sender : sender != peers.Host))
        {
            return false;
        }

        if (!remote.TryGetValue(line.Player, out Speaker? speaker))
        {
            speaker = new Speaker();
            remote.Add(line.Player, speaker);
        }
        else if (unchecked((int)(line.Sequence - speaker.Sequence)) <= 0)
        {
            return true;
        }

        speaker.Sequence = line.Sequence;
        float now = clock.Now;
        if (!speaker.Admit(now)) return true;

        // Vanilla has one bubble per character. A newer line replaces pending text during pose/view races.
        speaker.Pending = line;
        speaker.PendingUntil = now + PendingSeconds;
        if (peers.IsHost) Relay(frame, sender);
        return true;
    }

    private void Relay(byte[] frame, ulong except)
    {
        foreach (LaunchPlayer player in peers.Players)
        {
            if (player.SteamId != peers.LocalPlayer && player.SteamId != except)
            {
                peers.Send(player.SteamId, frame, reliable: true);
            }
        }
    }

    internal bool TryTakeLatest(ulong owner, out CharacterSpeech line)
    {
        line = default;
        if (!remote.TryGetValue(owner, out Speaker? speaker) || speaker.Pending is not { } pending) return false;
        speaker.Pending = null;
        if (clock.Now > speaker.PendingUntil) return false;
        line = pending;
        return true;
    }

    internal uint LastSequence(ulong owner) => remote.TryGetValue(owner, out Speaker? speaker) ? speaker.Sequence : 0;

    internal void Resume(uint sequence)
    {
        if (local.Sequence == 0 || unchecked((int)(sequence - local.Sequence)) > 0)
        {
            local.Sequence = sequence;
        }
    }

    internal void ClearPending(ulong owner = 0)
    {
        if (owner != 0)
        {
            if (remote.TryGetValue(owner, out Speaker? speaker)) speaker.Pending = null;
            return;
        }
        foreach (Speaker speaker in remote.Values) speaker.Pending = null;
    }
}
