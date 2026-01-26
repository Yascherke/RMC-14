using System.Collections.Generic;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;

namespace Content.Client._RMC14.Chemistry.UI.RecipeFinder;

public sealed class CompactOptionButton : OptionButton
{
    private static readonly StyleBoxFlat ButtonStyle = CreateStyleBox();
    private static readonly StyleBoxFlat PopupButtonStyle = CreatePopupStyleBox();

    public CompactOptionButton()
    {
        StyleBoxOverride = ButtonStyle;
        MinSize = new Vector2(0, 18);

        var label = FindChild<Label>(this);
        if (label != null)
        {
            label.Margin = new Thickness(4, 1, 4, 1);
            label.ClipText = true;
        }

        var triangle = FindChild<TextureRect>(this);
        if (triangle != null)
        {
            triangle.MinSize = new Vector2(8, 8);
            triangle.Margin = new Thickness(2, 0, 0, 0);
        }
    }

    public override void ButtonOverride(Button button)
    {
        button.StyleBoxOverride = PopupButtonStyle;
        button.MinSize = new Vector2(0, 18);
        button.ClipText = true;
        button.TextAlign = Label.AlignMode.Left;
        button.Label.Margin = new Thickness(4, 1, 4, 1);
    }

    private static StyleBoxFlat CreateStyleBox()
    {
        return new StyleBoxFlat
        {
            BackgroundColor = Color.FromHex("#2E313B"),
            BorderColor = Color.FromHex("#3C4350"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 1)
        };
    }

    private static StyleBoxFlat CreatePopupStyleBox()
    {
        return new StyleBoxFlat
        {
            BackgroundColor = Color.FromHex("#2B2E38"),
            BorderColor = Color.FromHex("#39414F"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 1)
        };
    }

    private static T? FindChild<T>(Control root) where T : Control
    {
        var queue = new Queue<Control>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is T match)
                return match;

            foreach (var child in current.Children)
            {
                queue.Enqueue(child);
            }
        }

        return null;
    }
}
