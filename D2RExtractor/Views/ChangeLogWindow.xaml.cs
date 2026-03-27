using System.Windows;

namespace D2RExtractor.Views;

public partial class ChangeLogWindow : Window
{
    public ChangeLogWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
