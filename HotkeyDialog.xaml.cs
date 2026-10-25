using System.Windows;
using System.Windows.Input;
using SamaDash.Services;

namespace SamaDash;

/// <summary>Cattura una nuova combinazione di tasti e prova a registrarla.</summary>
public partial class HotkeyDialog : Window
{
    readonly Func<HotKeyCombo?, bool> apply;

    /// <param name="apply">Registra la combinazione (null = disattiva); false se è già usata.</param>
    public HotkeyDialog(HotKeyCombo? current, Func<HotKeyCombo?, bool> apply)
    {
        InitializeComponent();
        this.apply = apply;
        ComboText.Text = current?.Display ?? "Nessuna";
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        // con Alt premuto WPF riporta Key.System e il tasto vero in SystemKey
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            DialogResult = false;
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
            or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var mods = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;
        var combo = new HotKeyCombo(mods, key);
        ComboText.Text = combo.Display;

        if (!combo.IsValid)
        {
            ShowMessage("Usa almeno un tasto tra Ctrl, Alt, Maiusc o Win (oppure un tasto F1–F12).");
            return;
        }
        if (!apply(combo))
        {
            ShowMessage($"{combo.Display} è già usata da Windows o da un'altra app: provane un'altra.");
            return;
        }
        DialogResult = true;
    }

    void ShowMessage(string text)
    {
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
    }

    void OnDisable(object sender, RoutedEventArgs e)
    {
        apply(null);
        DialogResult = true;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
