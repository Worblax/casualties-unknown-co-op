using System.Collections.Generic;
using CasualtiesCoop.Gameplay.Items;

namespace CasualtiesCoop.Core.Items;

internal enum CraftOperationState : byte
{
    Requested = 1,
    Reserved,
    Executing,
    Committed,
    Rejected,
    Cancelled,
    Expired
}

internal readonly struct CraftCreatedResult
{
    internal readonly ulong ClientKey;
    internal readonly ulong Item;
    internal readonly uint Revision;

    internal CraftCreatedResult(ulong clientKey, ulong item, uint revision)
    {
        ClientKey = clientKey;
        Item = item;
        Revision = revision;
    }
}

internal sealed class CraftOperationRecord
{
    internal readonly ulong Operation;
    internal readonly int Recipe;
    internal CraftOperationState State;
    internal ItemDecision Decision;
    internal readonly List<CraftCreatedResult> Created = new();

    internal CraftOperationRecord(ulong operation, int recipe, CraftOperationState state, ItemDecision decision)
    {
        Operation = operation;
        Recipe = recipe;
        State = state;
        Decision = decision;
    }

    internal bool Terminal => State is CraftOperationState.Committed or CraftOperationState.Rejected or
        CraftOperationState.Cancelled or CraftOperationState.Expired;
}