using System;

namespace CasualtiesCoop.Core;

internal interface ISessionTransport
{
    event Action<ulong, bool>? ConnectionChanged;

    // The sender is assigned by the verified connection. A single world owns this handler.
    Func<ulong, byte[], bool>? ReceiveFrame { get; set; }

    bool IsVerified(ulong peer);

    // A successful send means the transport took the frame, not that the peer processed it.
    bool TrySend(ulong peer, byte[] frame, bool reliable);
}
