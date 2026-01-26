using System;
using System.Collections.Generic;
using Content.Shared.Eui;
using Content.Shared.FixedPoint;
using Robust.Shared.Serialization;

namespace Content.Shared._RMC14.Chemistry.RecipeFinder;

[Serializable, NetSerializable]
public sealed class ChemRecipeEuiState(
    List<string> reagents,
    string selectedReagent,
    FixedPoint2 quantity,
    List<ChemRecipeRow> rows)
    : EuiStateBase
{
    public readonly List<string> Reagents = reagents;
    public readonly string SelectedReagent = selectedReagent;
    public readonly FixedPoint2 Quantity = quantity;
    public readonly List<ChemRecipeRow> Rows = rows;
}

[Serializable, NetSerializable]
public sealed class ChemRecipeRow(
    int depth,
    string reagentId,
    FixedPoint2 amount,
    bool catalyst,
    string selectedReactionId,
    List<string> reactionOptions,
    bool isCycle,
    bool isTruncated)
{
    public readonly int Depth = depth;
    public readonly string ReagentId = reagentId;
    public readonly FixedPoint2 Amount = amount;
    public readonly bool Catalyst = catalyst;
    public readonly string SelectedReactionId = selectedReactionId;
    public readonly List<string> ReactionOptions = reactionOptions;
    public readonly bool IsCycle = isCycle;
    public readonly bool IsTruncated = isTruncated;
}

public static class ChemRecipeEuiMsg
{
    [Serializable, NetSerializable]
    public sealed class SetTarget : EuiMessageBase
    {
        public readonly string ReagentId;
        public readonly FixedPoint2 Quantity;

        public SetTarget(string reagentId, FixedPoint2 quantity)
        {
            ReagentId = reagentId;
            Quantity = quantity;
        }
    }

    [Serializable, NetSerializable]
    public sealed class SelectReaction : EuiMessageBase
    {
        public readonly string ReagentId;
        public readonly string ReactionId;

        public SelectReaction(string reagentId, string reactionId)
        {
            ReagentId = reagentId;
            ReactionId = reactionId;
        }
    }
}
