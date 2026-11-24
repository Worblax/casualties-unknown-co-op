using System;
using System.Collections.Generic;

namespace CasualtiesCoop.Core;

internal sealed class FluidDrinkAuthority
{
    private const float Cooldown = 0.65f;
    private readonly IMonotonicClock clock;
    private readonly Dictionary<ulong, Receipt> receipts = new();

    internal readonly struct Receipt
    {
        internal readonly ulong Operation;
        internal readonly int X;
        internal readonly int Y;
        internal readonly byte Liquid;
        internal readonly float AcceptedAt;

        internal Receipt(ulong operation, int x, int y, byte liquid, float acceptedAt)
        {
            Operation = operation;
            X = x;
            Y = y;
            Liquid = liquid;
            AcceptedAt = acceptedAt;
        }
    }

    internal FluidDrinkAuthority(IMonotonicClock clock) => this.clock = clock;

    internal ulong LastOperation(ulong player) => receipts.TryGetValue(player, out Receipt receipt) ? receipt.Operation : 0;

    internal bool TryDecide(ulong player, ulong operation, int x, int y, byte expected, Func<bool> tryConsume,
        out Receipt receipt, out bool executed)
    {
        receipts.TryGetValue(player, out receipt);
        executed = operation > receipt.Operation;
        if (executed)
        {
            float now = clock.Now;
            bool accepted = expected != 0 && (receipt.Operation == 0 || now - receipt.AcceptedAt >= Cooldown) && tryConsume();
            receipt = new Receipt(operation, x, y, accepted ? expected : (byte)0, accepted ? now : receipt.AcceptedAt);
            receipts[player] = receipt;
        }

        return operation != 0 && receipt.Operation == operation;
    }
}
