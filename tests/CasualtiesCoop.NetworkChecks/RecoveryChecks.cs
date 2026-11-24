using CasualtiesCoop.Core;
using CasualtiesCoop.Core.Items;
using CasualtiesCoop.Gameplay;
using CasualtiesCoop.Gameplay.Items;
using CasualtiesCoop.Gameplay.Recovery;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;

internal static class RecoveryChecks
{
    private const ulong HostId = 1;
    private const ulong GuestId = 2;
    private static readonly Guid Session = new("ac258186-6f63-4cd8-ba89-23f8d8080932");
    private static readonly Guid PreviousSession = new("4d0c46af-4b98-43d2-bdca-2dfa41b124a1");
    private static readonly Guid Attempt = new("88745493-09f7-44ee-9204-de2af04620ff");
    private static readonly Guid OldAttempt = new("9c4c810e-2c12-4ebc-a170-923a6e604422");
    private static readonly RoomLaunchPlan Plan = new(Session, Attempt, "normal", RoomOptions.Sandbox,
        new[] { new LaunchPlayer(HostId, 0), new LaunchPlayer(GuestId, 0) });

    internal static void Run()
    {
        DisconnectDuringPickup(hostDecided: false);
        DisconnectDuringPickup(hostDecided: true);
        SessionReceiverLifetime();
    }

    private static void DisconnectDuringPickup(bool hostDecided)
    {
        using var scenario = new Scenario(hostDecided);
        scenario.QueuePickup();
        scenario.Network.Advance(0);
        Check.That(scenario.HostItem.Owner == (hostDecided ? scenario.Guest.LocalParticipant : default) && scenario.ReceivedResults == 0,
            "Disconnect point is before the request or after commit but before the result.");
        scenario.Network.SetConnected(HostId, GuestId, false);
        if (hostDecided)
        {
            scenario.RestartGuestItems();
        }
        scenario.Turn();
        Check.That(scenario.GuestRecovery.Waiting && !scenario.GuestItems.Synced, "A disconnected guest stays suspended.");
        scenario.Network.Advance(1);
        scenario.Network.SetConnected(HostId, GuestId, true);

        scenario.Host.Send(GuestId, ItemSnapshotWire.WriteDone(Session, 88, 0, 0));
        scenario.Host.Send(GuestId, SessionRejoinWire.WriteReference(MessageKind.SessionRejoinCommitted, PreviousSession, Attempt));
        scenario.Turn();
        Check.That(!scenario.GuestItems.Synced && scenario.GuestRecovery.Waiting,
            "Unsolicited completion and a committed frame from the previous session are ignored.");
        scenario.Turn();
        Check.That(scenario.GuestRecovery.Snapshot is { } snapshot &&
            snapshot.ItemOperation == (hostDecided ? 1ul : 0ul), "Rejoin reports the last operation decided by the host.");

        scenario.Guest.Send(HostId, SessionRejoinWire.WriteReference(MessageKind.SessionRejoinRequest, Session, OldAttempt));
        scenario.Guest.Send(HostId, SessionRejoinWire.WriteReference(MessageKind.SessionRejoinApplied, Session, OldAttempt));
        scenario.Network.Advance(1);
        Check.That(scenario.GuestItems.Synced && scenario.Replica.Owner == scenario.HostItem.Owner &&
            scenario.Replica.Revision == scenario.HostItem.Revision, "The snapshot restores the host's item state.");
        Check.That(scenario.CapturedOffers == 1 && scenario.HostRecovery.IsReturning(GuestId),
            "A competing attempt cannot replace the recovery offer or release the player.");

        if (hostDecided)
        {
            scenario.ProjectionAvailable = false;
            scenario.Turn();
            Check.That(scenario.HostRecovery.IsReturning(GuestId), "A missing owned projection blocks the recovery acknowledgement.");
            scenario.ProjectionAvailable = true;
        }

        scenario.Turn();
        Check.That(!scenario.HostRecovery.IsReturning(GuestId) && scenario.GuestRecovery.Waiting,
            "The guest stays suspended while the committed frame is in transit.");
        scenario.Network.Advance(1);
        scenario.Turn();
        Check.That(scenario.CommitFrames >= 2 && scenario.GuestRecovery.Waiting,
            "An applied frame is safely retried while awaiting the host confirmation.");
        scenario.Network.Advance(2);
        Check.That(!scenario.GuestRecovery.Waiting && scenario.ReceivedResults == 0 &&
            scenario.World.Pickups == (hostDecided ? 1 : 0),
            "Disconnect and rejoin settle one authoritative result without replaying the operation.");
    }

    private static void SessionReceiverLifetime()
    {
        var network = new SimulatedNetwork(19);
        var endpoint = network.AddPeer(GuestId);
        var sender = network.AddPeer(HostId);
        var oldSession = new SessionPeers(endpoint, Plan, GuestId, HostId);
        int oldCallbacks = 0;
        int newCallbacks = 0;
        oldSession.ConnectionChanged += (_, _) => oldCallbacks++;
        oldSession.AttachReceiver((_, _) => { oldCallbacks++; return true; });
        oldSession.Dispose();

        using var newSession = new SessionPeers(endpoint, Plan, GuestId, HostId);
        newSession.ConnectionChanged += (_, _) => newCallbacks++;
        newSession.AttachReceiver((_, _) => { newCallbacks++; return true; });
        oldSession.Dispose();
        network.SetConnected(HostId, GuestId, true);
        sender.TrySend(GuestId, new byte[] { 1 }, reliable: true);
        network.Advance(0);
        Check.That(oldCallbacks == 0 && newCallbacks == 2, "Retired sessions release the receiver and subscriptions.");
    }

    private sealed class Scenario : IDisposable
    {
        private readonly ulong itemId;
        private readonly HostItemSession hostItems;
        internal SimulatedNetwork Network { get; }
        internal SessionPeers Host { get; }
        internal SessionPeers Guest { get; }
        internal GuestRecovery GuestRecovery { get; }
        internal GuestItemSession GuestItems { get; private set; }
        internal HostRecovery HostRecovery { get; } = new();
        internal HostItemChecks.ItemWorld World { get; } = new();
        internal ItemState HostItem => hostItems.TryGet(itemId, out var state) ? state : throw new InvalidOperationException("Host item missing.");
        internal ItemState Replica => GuestItems.Replicas.TryGet(itemId, out var state) ? state : throw new InvalidOperationException("Replica missing.");
        internal bool ProjectionAvailable = true;
        internal int ReceivedResults;
        internal int CapturedOffers;
        internal int CommitFrames;

        internal Scenario(bool hostDecided)
        {
            Network = new SimulatedNetwork(11, script: frame => (MessageKind)frame.Data.Span[0] switch
            {
                MessageKind.ItemRequest or MessageKind.ItemResult => new NetworkFault(ExtraDelay: 0.5f),
                MessageKind.SessionRejoinCommitted when GameplayWire.HasHeader(frame.Data.Span, MessageKind.SessionRejoinCommitted, Session) =>
                    new NetworkFault(ExtraDelay: 2.5f),
                _ => default
            });
            Host = new SessionPeers(Network.AddPeer(HostId), Plan, HostId, HostId);
            Guest = new SessionPeers(Network.AddPeer(GuestId), Plan, GuestId, HostId);
            GuestRecovery = new GuestRecovery(Network.Clock);
            hostItems = new HostItemSession(Host, World, Network.Clock);
            hostItems.TryRegister("stick", default, 1, out itemId);
            GuestItems = MakeGuestItems();
            Host.AttachReceiver(ReceiveHost);
            Guest.AttachReceiver(ReceiveGuest);
            Network.SetConnected(HostId, GuestId, true);
            Host.ConnectionChanged += (peer, _) => HostRecovery.MarkReturning(peer);
            Guest.ConnectionChanged += (_, connected) =>
            {
                if (!connected)
                {
                    GuestRecovery.Begin(Attempt);
                }
            };
            GuestItems.Tick();
            Network.Advance(0);
            hostItems.TickSnapshots();
            Network.Advance(0);
            PickupAlreadyAcceptedOnWire = hostDecided;
        }

        private bool PickupAlreadyAcceptedOnWire { get; }

        internal void QueuePickup()
        {
            Check.That(GuestItems.TrySubmit(itemId, ItemAction.PickUp, 0, default), "Production guest queues the pickup.");
            if (PickupAlreadyAcceptedOnWire)
            {
                Network.Advance(0.5f);
            }
        }

        internal void RestartGuestItems()
        {
            GuestItems.Dispose();
            GuestItems = MakeGuestItems();
            GuestItems.SuspendForRecovery();
            Check.That(!GuestItems.Replicas.TryGet(itemId, out _), "A restarted guest begins with an empty replica store.");
        }

        private GuestItemSession MakeGuestItems()
        {
            var items = new GuestItemSession(Guest, Network.Clock);
            items.ResultAccepted += (_, _) => ReceivedResults++;
            return items;
        }

        internal void Turn()
        {
            bool applied = GuestItems.Synced && (Replica.Owner != Guest.LocalParticipant || ProjectionAvailable);
            GuestRecovery.Tick(applied, (kind, attempt) => Guest.Send(HostId, SessionRejoinWire.WriteReference(kind, Session, attempt)));
            GuestItems.Tick();
            Network.Advance(0);
            hostItems.TickSnapshots();
            Network.Advance(0);
        }

        private bool ReceiveHost(ulong sender, byte[] frame)
        {
            MessageKind kind = (MessageKind)frame[0];
            if (kind == MessageKind.SessionRejoinRequest)
            {
                Guid attempt = SessionRejoinWire.ReadReference(frame, kind, Session);
                byte[]? offer = HostRecovery.Offer(sender, attempt, () =>
                {
                    CapturedOffers++;
                    return new SessionRejoinState(Plan, attempt, 13, 12, 11, hostItems.LastOperation(sender), 0, 0, 0, false);
                }, out _);
                if (offer is not null)
                {
                    Host.Send(sender, offer);
                }
            }
            else if (kind == MessageKind.SessionRejoinApplied)
            {
                Guid attempt = SessionRejoinWire.ReadReference(frame, kind, Session);
                HostRecovery.TryCommit(sender, attempt, target =>
                {
                    CommitFrames++;
                    return Host.Send(target, SessionRejoinWire.WriteReference(MessageKind.SessionRejoinCommitted, Session, attempt));
                });
            }
            else if (HostRecovery.AllowsGameplay(sender, kind))
            {
                return hostItems.Receive(sender, frame, worldReady: true);
            }
            return true;
        }

        private bool ReceiveGuest(ulong sender, byte[] frame)
        {
            if (sender != HostId)
            {
                return false;
            }

            MessageKind kind = (MessageKind)frame[0];
            if (kind == MessageKind.SessionRejoinState)
            {
                if (SessionRejoinWire.TryReadState(frame, Session, out SessionRejoinState state) &&
                    GuestRecovery.ExpectsState(state.Attempt))
                {
                    GuestRecovery.RestoreFrom(state);
                    GuestItems.ResumeAfterOffer(state.ItemOperation);
                }
            }
            else if (kind == MessageKind.SessionRejoinCommitted)
            {
                GuestRecovery.TryCommit(SessionRejoinWire.ReadReference(frame, kind, Session));
            }
            else
            {
                return GuestItems.Receive(sender, frame);
            }
            return true;
        }

        public void Dispose()
        {
            GuestItems.Dispose();
            hostItems.Dispose();
            Guest.Dispose();
            Host.Dispose();
        }
    }
}
