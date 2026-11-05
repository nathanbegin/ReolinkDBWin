using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReolinkDBWin.Services;

namespace ReolinkDBWin.Controls;

/// <summary>Champ de saisie d'un raccourci clavier : capture la combinaison pressée.</summary>
public sealed class HotkeyBox : TextBox
{
    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        ContextMenu = null;
        Cursor = Cursors.Hand;
    }

    public Hotkey Hotkey
    {
        get => Hotkey.Parse(Text);
        set => Text = value.ToString();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            Text = "";
            return;
        }
        if (key is Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = false; // navigation normale
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
            return;
        var mods = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;
        var isFunctionKey = key is >= Key.F1 and <= Key.F24;
        if (mods == ModifierKeys.None && !isFunctionKey) return; // un raccourci global sans modificateur gênerait la saisie
        Text = new Hotkey(mods, key).ToString();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        ToolTip = "Appuyez sur la combinaison de touches…";
    }
}
