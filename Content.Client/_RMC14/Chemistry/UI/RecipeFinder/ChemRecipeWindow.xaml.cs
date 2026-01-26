using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.FixedPoint;
using Content.Shared._RMC14.Chemistry.Reagent;
using Content.Shared._RMC14.Chemistry.RecipeFinder;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.XAML;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Client._RMC14.Chemistry.UI.RecipeFinder;

public sealed partial class ChemRecipeWindow : FancyWindow
{
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IEntityManager _entities = default!;

    private ChemRecipeEui? _eui;
    private bool _suppressEvents;

    private List<string> _allReagents = new();
    private string _selectedReagentId = string.Empty;
    private readonly HashSet<string> _collapsedChains = new();

    private RMCReagentSystem Reagents => _entities.System<RMCReagentSystem>();

    private readonly LineEdit _searchReagent;
    private readonly PanelContainer _targetColor;
    private readonly OptionButton _targetReagent;
    private readonly OptionButton _favoriteReagent;
    private readonly Button _favoriteToggle;
    private readonly FloatSpinBox _targetAmount;
    private readonly Button _refreshButton;
    private readonly BoxContainer _recipeRows;
    private readonly Label _emptyLabel;

    private readonly List<string> _favoriteReagents = new();

    public ChemRecipeWindow()
    {
        IoCManager.InjectDependencies(this);
        RobustXamlLoader.Load(this);

        _searchReagent = FindControl<LineEdit>("SearchReagent");
        _targetColor = FindControl<PanelContainer>("TargetColor");
        _targetReagent = FindControl<OptionButton>("TargetReagent");
        _favoriteReagent = FindControl<OptionButton>("FavoriteReagent");
        _favoriteToggle = FindControl<Button>("FavoriteToggle");
        _targetAmount = FindControl<FloatSpinBox>("TargetAmount");
        _refreshButton = FindControl<Button>("RefreshButton");
        _recipeRows = FindControl<BoxContainer>("RecipeRows");
        _emptyLabel = FindControl<Label>("EmptyLabel");

        _searchReagent.OnTextChanged += _ => ApplySearchFilter();
        _favoriteReagent.OnItemSelected += OnFavoriteSelected;
        _favoriteToggle.OnPressed += _ => ToggleFavorite();
        _targetReagent.OnItemSelected += OnTargetSelected;
        _targetAmount.OnValueChanged += _ => SendTarget();
        _refreshButton.OnPressed += _ => SendTarget();
        _targetAmount.IsValid = value => value >= 0.1f;
    }

    public ChemRecipeWindow(ChemRecipeEui eui) : this()
    {
        _eui = eui;
    }

    public void SetEui(ChemRecipeEui eui)
    {
        _eui = eui;
    }

    public void SetState(ChemRecipeEuiState state)
    {
        _suppressEvents = true;
        _allReagents = state.Reagents;
        _selectedReagentId = state.SelectedReagent;
        ApplySearchFilter();
        UpdateFavoriteOptions();
        UpdateFavoriteButton();
        _targetAmount.Value = state.Quantity.Float();
        UpdateRows(state.Rows);
        UpdateTargetColor(state.SelectedReagent);
        _suppressEvents = false;
    }

    private void UpdateReagentOptions(List<string> reagents, string selectedId)
    {
        _targetReagent.Clear();
        var ordered = reagents
            .Select(id => (Id: id, Name: GetReagentName(id)))
            .OrderBy(entry => entry.Name.ToLowerInvariant())
            .ThenBy(entry => entry.Id.ToLowerInvariant())
            .ToList();

        var selectedIndex = -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            _targetReagent.AddItem(entry.Name, i);
            _targetReagent.SetItemMetadata(i, entry.Id);

            if (selectedIndex < 0 &&
                string.Equals(entry.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i;
            }
        }

        if (selectedIndex >= 0)
            _targetReagent.SelectId(selectedIndex);

        UpdateTargetColor(selectedId);
        UpdateFavoriteButton();
    }

    private void ApplySearchFilter()
    {
        var searchText = _searchReagent.Text;
        var filtered = FilterReagents(_allReagents, searchText);
        var searchActive = !string.IsNullOrWhiteSpace(searchText);

        if (searchActive && filtered.Count > 0)
        {
            _selectedReagentId = PickSearchMatch(filtered, searchText) ?? filtered[0];
        }

        if (!string.IsNullOrWhiteSpace(_selectedReagentId) &&
            !ContainsReagentIgnoreCase(filtered, _selectedReagentId))
        {
            filtered.Add(_selectedReagentId);
        }

        UpdateReagentOptions(filtered, _selectedReagentId);

        if (searchActive && filtered.Count > 0 && !_suppressEvents)
            SendTarget();
    }

    private List<string> FilterReagents(List<string> reagents, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return new List<string>(reagents);

        var term = search.Trim();
        var results = new List<string>();

        foreach (var id in reagents)
        {
            var name = GetReagentName(id);
            if (id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(id);
            }
        }

        return results;
    }

    private static bool ContainsReagentIgnoreCase(List<string> reagents, string reagentId)
    {
        foreach (var id in reagents)
        {
            if (string.Equals(id, reagentId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void UpdateFavoriteOptions()
    {
        _favoriteReagent.Clear();
        var ordered = _favoriteReagents
            .Select(id => (Id: id, Name: GetReagentName(id)))
            .OrderBy(entry => entry.Name.ToLowerInvariant())
            .ThenBy(entry => entry.Id.ToLowerInvariant())
            .ToList();

        _favoriteReagent.Disabled = ordered.Count == 0;

        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            _favoriteReagent.AddItem(entry.Name, i);
            _favoriteReagent.SetItemMetadata(i, entry.Id);
        }
    }

    private void UpdateFavoriteButton()
    {
        if (string.IsNullOrWhiteSpace(_selectedReagentId))
        {
            _favoriteToggle.Text = Loc.GetString("chem-recipe-eui-favorite-add");
            return;
        }

        _favoriteToggle.Text = IsFavorite(_selectedReagentId)
            ? Loc.GetString("chem-recipe-eui-favorite-remove")
            : Loc.GetString("chem-recipe-eui-favorite-add");
    }

    private void ToggleFavorite()
    {
        if (string.IsNullOrWhiteSpace(_selectedReagentId))
            return;

        if (IsFavorite(_selectedReagentId))
            RemoveFavorite(_selectedReagentId);
        else
            AddFavorite(_selectedReagentId);

        UpdateFavoriteOptions();
        UpdateFavoriteButton();
    }

    private bool IsFavorite(string reagentId)
    {
        foreach (var id in _favoriteReagents)
        {
            if (string.Equals(id, reagentId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void AddFavorite(string reagentId)
    {
        if (IsFavorite(reagentId))
            return;

        _favoriteReagents.Add(reagentId);
    }

    private void RemoveFavorite(string reagentId)
    {
        for (var i = _favoriteReagents.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_favoriteReagents[i], reagentId, StringComparison.OrdinalIgnoreCase))
                _favoriteReagents.RemoveAt(i);
        }
    }

    private string? PickSearchMatch(List<string> reagents, string searchText)
    {
        var term = searchText.Trim();
        if (term.Length == 0)
            return null;

        foreach (var id in reagents)
        {
            if (string.Equals(id, term, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetReagentName(id), term, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        foreach (var id in reagents)
        {
            if (id.StartsWith(term, StringComparison.OrdinalIgnoreCase) ||
                GetReagentName(id).StartsWith(term, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return null;
    }

    private void UpdateRows(List<ChemRecipeRow> rows)
    {
        _recipeRows.DisposeAllChildren();
        _emptyLabel.Visible = rows.Count == 0;

        if (rows.Count == 0)
            return;

        var stack = new Stack<(int Depth, BoxContainer Container, string PathKey)>();
        stack.Push((-1, _recipeRows, string.Empty));

        var path = new List<string>();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            while (stack.Count > 0 && stack.Peek().Depth >= row.Depth)
                stack.Pop();

            if (stack.Count == 0)
                stack.Push((-1, _recipeRows, string.Empty));

            UpdatePath(path, row.Depth, row.ReagentId);
            var pathKey = BuildPathKey(path, row.Depth);
            var hasChildren = i + 1 < rows.Count && rows[i + 1].Depth > row.Depth;

            var rowControl = BuildRow(row, hasChildren, pathKey, out var childContainer);
            stack.Peek().Container.AddChild(rowControl);
            stack.Push((row.Depth, childContainer, pathKey));
        }
    }

    private Control BuildRow(ChemRecipeRow row, bool hasChildren, string pathKey, out BoxContainer childContainer)
    {
        var outer = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true
        };

        var panel = new PanelContainer
        {
            Margin = new Thickness(0, 0, 0, 0),
            HorizontalExpand = true
        };
        panel.StyleClasses.Add("Inset");

        var body = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            Margin = new Thickness(2, 1),
            HorizontalExpand = true
        };

        var defaultCollapsed = hasChildren &&
                               (row.Depth > 0 || string.IsNullOrWhiteSpace(row.SelectedReactionId));
        var collapsed = hasChildren && GetChainCollapsed(pathKey, defaultCollapsed);

        childContainer = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            Margin = new Thickness(0, 0, 0, 0),
            VerticalExpand = true,
            HorizontalExpand = true
        };

        var chainHost = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            Visible = hasChildren && !collapsed,
            HorizontalExpand = true
        };

        var linkBar = new PanelContainer
        {
            MinSize = new Vector2(2, 0),
            Margin = new Thickness(1, 0, 3, 0),
            VerticalExpand = true,
            PanelOverride = MakeChainBarStyle()
        };

        chainHost.AddChild(linkBar);
        chainHost.AddChild(childContainer);

        var header = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true
        };

        var collapseButton = new Button
        {
            Text = hasChildren ? (collapsed ? ">" : "v") : "-",
            MinSize = new Vector2(12, 12),
            Margin = new Thickness(0, 0, 2, 0),
            Disabled = !hasChildren
        };

        if (hasChildren)
        {
            var normalizedKey = NormalizeKey(pathKey);
            var capturedHost = chainHost;
            collapseButton.OnPressed += _ =>
            {
                var nowCollapsed = !_collapsedChains.Contains(normalizedKey);
                if (nowCollapsed)
                    _collapsedChains.Add(normalizedKey);
                else
                    _collapsedChains.Remove(normalizedKey);

                capturedHost.Visible = !nowCollapsed;
                collapseButton.Text = nowCollapsed ? ">" : "v";
            };
        }

        header.AddChild(collapseButton);
        header.AddChild(MakeColorSwatch(GetReagentColor(row.ReagentId)));

        var nameLabel = new Label
        {
            Text = $"{GetReagentName(row.ReagentId)} ({row.ReagentId})",
            HorizontalExpand = true
        };

        var amountLabel = new Label
        {
            Text = row.Amount.ToString(),
            StyleClasses = { "LabelSubText" }
        };

        header.AddChild(nameLabel);
        header.AddChild(amountLabel);

        var isBase = !row.Catalyst && row.ReactionOptions.Count == 0;
        var isBaseSelected = !row.Catalyst && row.ReactionOptions.Count > 0 &&
                             string.IsNullOrWhiteSpace(row.SelectedReactionId);

        if (row.Catalyst)
            header.AddChild(MakeTagLabel(Loc.GetString("chem-recipe-eui-catalyst")));
        else if (row.IsCycle)
            header.AddChild(MakeTagLabel(Loc.GetString("chem-recipe-eui-cycle")));
        else if (row.IsTruncated)
            header.AddChild(MakeTagLabel(Loc.GetString("chem-recipe-eui-truncated")));
        else if (isBase)
            header.AddChild(MakeTagLabel(Loc.GetString("chem-recipe-eui-base-reagent")));
        else if (isBaseSelected)
            header.AddChild(MakeTagLabel(Loc.GetString("chem-recipe-eui-base")));

        body.AddChild(header);

        var components = BuildDirectComponentsLabel(row);
        if (components != null)
        {
            body.AddChild(components);
        }
        if (!row.Catalyst && row.ReactionOptions.Count > 0)
            body.AddChild(BuildReactionPicker(row));

        panel.AddChild(body);
        outer.AddChild(panel);
        outer.AddChild(chainHost);
        return outer;
    }

    private Control BuildReactionPicker(ChemRecipeRow row)
    {
        var reactionRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            Margin = new Thickness(0, 0, 0, 0)
        };

        reactionRow.AddChild(new Label
        {
            Text = Loc.GetString("chem-recipe-eui-via"),
            Margin = new Thickness(0, 0, 3, 0),
            StyleClasses = { "LabelSubText" }
        });

        var option = new CompactOptionButton
        {
            HorizontalExpand = true
        };

        option.AddItem(Loc.GetString("chem-recipe-eui-base"), 0);
        option.SetItemMetadata(0, string.Empty);

        var selectedIndex = 0;
        for (var i = 0; i < row.ReactionOptions.Count; i++)
        {
            var reactionId = row.ReactionOptions[i];
            var itemIndex = i + 1;
            option.AddItem(GetReactionName(reactionId), itemIndex);
            option.SetItemMetadata(itemIndex, reactionId);

            if (string.Equals(reactionId, row.SelectedReactionId, StringComparison.OrdinalIgnoreCase))
                selectedIndex = itemIndex;
        }

        option.SelectId(selectedIndex);
        option.OnItemSelected += args =>
        {
            if (_suppressEvents)
                return;

            option.SelectId(args.Id);
            var selected = option.SelectedMetadata as string ?? string.Empty;
            _eui?.SelectReaction(row.ReagentId, selected);
        };

        reactionRow.AddChild(option);
        return reactionRow;
    }

    private Control? BuildDirectComponentsLabel(ChemRecipeRow row)
    {
        if (row.Catalyst || string.IsNullOrWhiteSpace(row.SelectedReactionId))
            return null;

        if (!_prototype.TryIndex<ReactionPrototype>(row.SelectedReactionId, out var reaction))
            return null;

        if (!reaction.Products.TryGetValue(row.ReagentId, out var productAmount) ||
            productAmount <= FixedPoint2.Zero)
        {
            return null;
        }

        var unitReactions = row.Amount / productAmount;
        var components = new List<string>();

        foreach (var (reactantId, reactant) in reaction.Reactants
                     .OrderBy(entry => GetReagentName(entry.Key).ToLowerInvariant())
                     .ThenBy(entry => entry.Key.ToLowerInvariant()))
        {
            var required = reactant.Amount * unitReactions;
            var component = $"{GetReagentName(reactantId)} x{required}";

            if (reactant.Catalyst)
                component += $" ({Loc.GetString("chem-recipe-eui-catalyst")})";

            components.Add(component);
        }

        if (components.Count == 0)
            return null;

        var prefix = Loc.GetString("chem-recipe-eui-direct-components");
        return new Label
        {
            Text = $"{prefix} {string.Join(", ", components)}",
            StyleClasses = { "LabelSubText" }
        };
    }

    private bool GetChainCollapsed(string pathKey, bool defaultCollapsed)
    {
        var normalized = NormalizeKey(pathKey);
        if (_collapsedChains.Contains(normalized))
            return true;

        if (!defaultCollapsed)
            return false;

        _collapsedChains.Add(normalized);
        return true;
    }

    private static string NormalizeKey(string key)
    {
        return key.ToLowerInvariant();
    }

    private static void UpdatePath(List<string> path, int depth, string reagentId)
    {
        while (path.Count <= depth)
            path.Add(string.Empty);

        path[depth] = reagentId;

        if (path.Count > depth + 1)
            path.RemoveRange(depth + 1, path.Count - depth - 1);
    }

    private static string BuildPathKey(List<string> path, int depth)
    {
        if (depth < 0 || path.Count == 0)
            return string.Empty;

        return string.Join(">", path.Take(depth + 1));
    }

    private void UpdateTargetColor(string reagentId)
    {
        if (string.IsNullOrWhiteSpace(reagentId) || !Reagents.TryIndex(reagentId, out var reagent))
        {
            _targetColor.Visible = false;
            _targetColor.PanelOverride = null;
            return;
        }

        _targetColor.Visible = true;
        _targetColor.PanelOverride = MakeColorStyle(reagent.SubstanceColor);
    }

    private Color GetReagentColor(string reagentId)
    {
        if (Reagents.TryIndex(reagentId, out var reagent))
            return reagent.SubstanceColor;

        return Color.White;
    }

    private static PanelContainer MakeColorSwatch(Color color)
    {
        return new PanelContainer
        {
            MinSize = new Vector2(8, 8),
            MaxSize = new Vector2(8, 8),
            Margin = new Thickness(0, 1, 3, 0),
            PanelOverride = MakeColorStyle(color)
        };
    }

    private static StyleBoxFlat MakeColorStyle(Color color)
    {
        return new StyleBoxFlat
        {
            BackgroundColor = color,
            BorderColor = Color.Black,
            BorderThickness = new Thickness(1)
        };
    }

    private static StyleBoxFlat MakeChainBarStyle()
    {
        return new StyleBoxFlat
        {
            BackgroundColor = Color.FromHex("#2B2F3A"),
            BorderColor = Color.FromHex("#3A4150"),
            BorderThickness = new Thickness(1, 0, 0, 0)
        };
    }

    private void OnTargetSelected(OptionButton.ItemSelectedEventArgs args)
    {
        if (_suppressEvents)
            return;

        _targetReagent.SelectId(args.Id);
        _selectedReagentId = _targetReagent.SelectedMetadata as string ?? _selectedReagentId;
        UpdateTargetColor(_selectedReagentId);
        UpdateFavoriteButton();
        SendTarget();
    }

    private void OnFavoriteSelected(OptionButton.ItemSelectedEventArgs args)
    {
        if (_suppressEvents)
            return;

        _favoriteReagent.SelectId(args.Id);
        var reagentId = _favoriteReagent.SelectedMetadata as string;
        if (string.IsNullOrWhiteSpace(reagentId))
            return;

        _selectedReagentId = reagentId;
        UpdateTargetColor(reagentId);
        UpdateFavoriteButton();
        SendTarget();
    }

    private void SendTarget()
    {
        if (_suppressEvents)
            return;

        var reagentId = _targetReagent.SelectedMetadata as string;
        if (string.IsNullOrWhiteSpace(reagentId))
            return;

        _selectedReagentId = reagentId;
        UpdateTargetColor(reagentId);
        UpdateFavoriteButton();
        var quantity = FixedPoint2.New(_targetAmount.Value);
        _eui?.SetTarget(reagentId, quantity);
    }

    private string GetReagentName(string reagentId)
    {
        if (Reagents.TryIndex(reagentId, out var reagent))
            return reagent.LocalizedName;

        return reagentId;
    }

    private string GetReactionName(string reactionId)
    {
        if (_prototype.TryIndex<ReactionPrototype>(reactionId, out var reaction))
        {
            if (!string.IsNullOrWhiteSpace(reaction.Name))
            {
                if (string.Equals(reaction.Name, reactionId, StringComparison.OrdinalIgnoreCase))
                    return reaction.Name;

                return $"{reaction.Name} ({reactionId})";
            }
        }

        return reactionId;
    }

    private static Label MakeTagLabel(string text)
    {
        return new Label
        {
            Text = text,
            Margin = new Thickness(6, 0, 0, 0),
            StyleClasses = { "LabelSubText" }
        };
    }
}
