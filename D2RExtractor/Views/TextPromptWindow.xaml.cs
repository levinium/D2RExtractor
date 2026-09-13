using System.Windows;
using System.Windows.Input;

// UseWindowsForms is on for the folder browser, so these are ambiguous unqualified.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;

namespace D2RExtractor.Views;

/// <summary>A one-line text prompt, since WPF has no InputBox.</summary>
public partial class TextPromptWindow : Window
{
    /// <summary>What was typed. Only meaningful when ShowDialog returned true.</summary>
    public string Value => ValueBox.Text;

    public TextPromptWindow(string title, string prompt, string initial)
    {
        InitializeComponent();

        Title = title;
        PromptLabel.Text = prompt;
        ValueBox.Text = initial;

        // Selected, not just focused: renaming usually means replacing, and the alternative is
        // everyone deleting the old name by hand first.
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    private void ValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { DialogResult = true; e.Handled = true; }
        else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
