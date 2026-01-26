using Content.Client.Eui;
using Content.Shared._RMC14.Chemistry.RecipeFinder;
using Content.Shared.Eui;
using Content.Shared.FixedPoint;
using JetBrains.Annotations;

namespace Content.Client._RMC14.Chemistry.UI.RecipeFinder;

[UsedImplicitly]
public sealed class ChemRecipeEui : BaseEui
{
    private readonly ChemRecipeWindow _window;

    public ChemRecipeEui()
    {
        _window = new ChemRecipeWindow(this);
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
    }

    public override void Opened()
    {
        base.Opened();
        _window.OpenCentered();
    }

    public override void Closed()
    {
        base.Closed();
        _window.Close();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is not ChemRecipeEuiState cast)
            return;

        _window.SetState(cast);
    }

    public void SetTarget(string reagentId, FixedPoint2 quantity)
    {
        SendMessage(new ChemRecipeEuiMsg.SetTarget(reagentId, quantity));
    }

    public void SelectReaction(string reagentId, string reactionId)
    {
        SendMessage(new ChemRecipeEuiMsg.SelectReaction(reagentId, reactionId));
    }
}
