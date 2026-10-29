using System.Windows.Controls;
using System.Windows.Input;
using Cockpit.UI;

namespace Cockpit.Wpf;

/// <summary>
/// Prompt box keys as in the Claude app: Enter sends, Shift+Enter starts a new line, a multi-line paste keeps every line
/// (the box accepts returns), Up in an empty box brings back the last prompt to edit, Esc stops a running prompt.
/// </summary>
internal static class ComposerKeys
{
    public static void Attach(TextBox box, CockpitOverlayViewModel vm)
    {
        box.AcceptsReturn = true;
        box.PreviewKeyDown += (_, e) =>
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (e.Key)
            {
                case Key.Enter when !shift && (Keyboard.Modifiers & ModifierKeys.Control) == 0:
                    e.Handled = true;
                    if (vm.SubmitPromptCommand.CanExecute(null)) vm.SubmitPromptCommand.Execute(null);
                    break;
                case Key.Up when box.Text.Length == 0:
                    if (vm.RecallLastPrompt())
                    {
                        e.Handled = true;
                        box.CaretIndex = box.Text.Length;
                    }
                    break;
                case Key.Escape when vm.StopCommand.CanExecute(null):
                    e.Handled = true;
                    vm.StopCommand.Execute(null);
                    break;
            }
        };
    }
}
