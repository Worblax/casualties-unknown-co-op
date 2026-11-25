using System.Collections.Generic;
using CasualtiesCoop.Core.Session;
using CasualtiesCoop.Gameplay.Items;

namespace CasualtiesCoop.Core.Items;

internal sealed class ItemOperationReceipts
{
    // One result per launch seat. A guest retries its only pending operation with the same number.
    // A newer operation replaces the receipt, so requests below it cannot be executed or replayed.
    private readonly Dictionary<ParticipantId, Receipt> latest = new();

    internal readonly struct Receipt
    {
        internal readonly ulong Operation;
        internal readonly ulong ItemId;
        internal readonly ItemAction Action;
        internal readonly ItemDecision Decision;

        internal Receipt(SharedItemRequest request, ItemDecision decision)
        {
            Operation = request.Operation;
            ItemId = request.ItemId;
            Action = request.Action;
            Decision = decision;
        }
    }

    internal bool TryGet(ParticipantId player, out Receipt receipt) => latest.TryGetValue(player, out receipt);

    internal void Record(ParticipantId player, Receipt receipt) => latest[player] = receipt;
}
