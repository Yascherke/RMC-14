using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.EUI;
using Content.Shared.Chemistry.Reaction;
using Content.Shared._RMC14.Chemistry.Reagent;
using Content.Shared._RMC14.Chemistry.RecipeFinder;
using Content.Shared.Eui;
using Content.Shared.FixedPoint;
using JetBrains.Annotations;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._RMC14.Chemistry.UI;

[UsedImplicitly]
public sealed class ChemRecipeEui : BaseEui
{
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IEntityManager _entities = default!;

    private readonly Dictionary<string, List<ReactionPrototype>> _reactionsByProduct = new();
    private readonly Dictionary<string, string> _selectedReactions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _reagentIds = new();

    private string _selectedReagent = string.Empty;
    private FixedPoint2 _quantity = FixedPoint2.New(10);

    private const int MaxDepth = 7;

    private RMCReagentSystem Reagents => _entities.System<RMCReagentSystem>();

    public ChemRecipeEui()
    {
        IoCManager.InjectDependencies(this);
    }

    public override void Opened()
    {
        base.Opened();
        BuildCaches();
        ChooseDefaultTarget();
        StateDirty();
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        switch (msg)
        {
            case ChemRecipeEuiMsg.SetTarget set:
                if (!Reagents.TryIndex(set.ReagentId, out _))
                    return;

                _selectedReagent = set.ReagentId;
                _quantity = ClampQuantity(set.Quantity);
                StateDirty();
                break;
            case ChemRecipeEuiMsg.SelectReaction select:
                if (!Reagents.TryIndex(select.ReagentId, out _))
                    return;

                _selectedReactions[select.ReagentId] = select.ReactionId ?? string.Empty;

                StateDirty();
                break;
        }
    }

    public override EuiStateBase GetNewState()
    {
        var rows = new List<ChemRecipeRow>();
        if (!string.IsNullOrWhiteSpace(_selectedReagent))
            BuildRows(_selectedReagent, _quantity, 0, rows, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        return new ChemRecipeEuiState(new List<string>(_reagentIds), _selectedReagent, _quantity, rows);
    }

    private void BuildCaches()
    {
        _reactionsByProduct.Clear();
        _reagentIds.Clear();

        foreach (var reagent in Reagents.Enumerate())
        {
            if (reagent.Abstract)
                continue;

            _reagentIds.Add(reagent.ID);
        }

        _reagentIds.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var reaction in _prototype.EnumeratePrototypes<ReactionPrototype>())
        {
            foreach (var product in reaction.Products.Keys)
            {
                if (!_reactionsByProduct.TryGetValue(product, out var list))
                {
                    list = new List<ReactionPrototype>();
                    _reactionsByProduct[product] = list;
                }

                list.Add(reaction);
            }
        }

        foreach (var list in _reactionsByProduct.Values)
        {
            list.Sort((a, b) => a.CompareTo(b));
        }
    }

    private void ChooseDefaultTarget()
    {
        if (!string.IsNullOrWhiteSpace(_selectedReagent) &&
            Reagents.TryIndex(_selectedReagent, out _))
        {
            return;
        }

        if (Reagents.TryIndex("Bicaridine", out _))
        {
            _selectedReagent = "Bicaridine";
            return;
        }

        if (_reagentIds.Count > 0)
            _selectedReagent = _reagentIds[0];
    }

    private static FixedPoint2 ClampQuantity(FixedPoint2 quantity)
    {
        if (quantity <= FixedPoint2.Zero)
            return FixedPoint2.New(1);

        return quantity;
    }

    private void BuildRows(
        string reagentId,
        FixedPoint2 amount,
        int depth,
        List<ChemRecipeRow> rows,
        HashSet<string> visited,
        bool forceLeaf = false)
    {
        var isCycle = visited.Contains(reagentId);
        var isTruncated = depth >= MaxDepth;

        var reactionOptions = forceLeaf ? new List<string>() : GetReactionOptions(reagentId);
        var selectedReaction = string.Empty;

        if (!forceLeaf && !isCycle && !isTruncated)
            selectedReaction = ResolveSelection(reagentId, reactionOptions);

        rows.Add(new ChemRecipeRow(
            depth,
            reagentId,
            amount,
            forceLeaf,
            selectedReaction,
            reactionOptions,
            isCycle,
            isTruncated));

        if (forceLeaf || isCycle || isTruncated || string.IsNullOrWhiteSpace(selectedReaction))
            return;

        if (!_reactionsByProduct.TryGetValue(reagentId, out var reactions))
            return;

        var reaction = reactions.FirstOrDefault(r => r.ID == selectedReaction);
        if (reaction == null)
            return;

        if (!reaction.Products.TryGetValue(reagentId, out var productAmount) ||
            productAmount <= FixedPoint2.Zero)
        {
            return;
        }

        var unitReactions = amount / productAmount;
        visited.Add(reagentId);

        foreach (var (reactantId, reactant) in reaction.Reactants)
        {
            var required = reactant.Amount * unitReactions;
            BuildRows(reactantId, required, depth + 1, rows, visited, reactant.Catalyst);
        }

        visited.Remove(reagentId);
    }

    private List<string> GetReactionOptions(string reagentId)
    {
        if (!_reactionsByProduct.TryGetValue(reagentId, out var reactions))
            return new List<string>();

        return reactions.Select(r => r.ID).ToList();
    }

    private string ResolveSelection(string reagentId, List<string> options)
    {
        if (options.Count == 0)
            return string.Empty;

        if (_selectedReactions.TryGetValue(reagentId, out var selected))
        {
            if (string.IsNullOrWhiteSpace(selected))
                return string.Empty;

            if (options.Contains(selected))
                return selected;
        }

        return options[0];
    }
}
