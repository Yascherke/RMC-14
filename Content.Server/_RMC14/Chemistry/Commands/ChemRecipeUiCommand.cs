using Content.Server._RMC14.Chemistry.UI;
using Content.Server.Administration;
using Content.Server.EUI;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._RMC14.Chemistry.Commands;

[AdminCommand(AdminFlags.Debug)]
public sealed class OpenChemRecipeUiCommand : LocalizedEntityCommands
{
    [Dependency] private readonly EuiManager _euiManager = default!;

    public override string Command => "chemrecipeui";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var player = shell.Player;
        if (player == null)
        {
            shell.WriteError(Loc.GetString("shell-cannot-run-command-from-server"));
            return;
        }

        _euiManager.OpenEui(new ChemRecipeEui(), player);
    }
}
