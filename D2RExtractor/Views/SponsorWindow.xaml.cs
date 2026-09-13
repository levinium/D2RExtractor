using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using D2RExtractor.Services;

// UseWindowsForms is on for the folder browser, so a bare Button is ambiguous.
using Button = System.Windows.Controls.Button;

namespace D2RExtractor.Views;

/// <summary>
/// The one place D2R File Extractor asks for anything.
/// <para>
/// Reachable only by pressing a heart nobody has to press, so the honest
/// version of this is a clear ask, a sensible default, and a door that is
/// obviously open.
/// </para>
/// </summary>
public partial class SponsorWindow : Window
{
    private readonly string _template;
    private readonly bool _showAmounts;

    private decimal? _chosen;
    private bool _isOther;

    public SponsorWindow(string template)
    {
        InitializeComponent();

        _template = template;
        _showAmounts = SponsorLink.CarriesAmount(template);

        // Only where the destination takes the amount in its link. Picking $5
        // and landing somewhere that never heard of it is worse than not being
        // asked at all.
        if (_showAmounts)
        {
            BuildChips();
            _chosen = SponsorLink.Default;
        }
        else
        {
            AmountPanel.Visibility = Visibility.Collapsed;
        }

        Refresh();
    }

    private void BuildChips()
    {
        foreach (var amount in SponsorLink.Suggested)
        {
            var chip = new Button
            {
                Content = SponsorLink.Describe(amount),
                Style = (Style)FindResource("AmountChipStyle"),
                DataContext = amount,
            };

            chip.Click += (_, _) =>
            {
                _isOther = false;
                _chosen = amount;
                OtherAmountBox.Visibility = Visibility.Collapsed;
                Refresh();
            };

            ChipRow.Children.Add(chip);
        }
    }

    private void OtherChip_Click(object sender, RoutedEventArgs e)
    {
        _isOther = true;
        OtherAmountBox.Visibility = Visibility.Visible;
        OtherAmountBox.Focus();
        Refresh();
    }

    private void OtherAmountBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    /// <summary>
    /// Repaints which chip is chosen and what the action button says. Naming the
    /// sum on the button means nobody has to look back up at the row of chips to
    /// check what they are about to be charged.
    /// </summary>
    private void Refresh()
    {
        if (_showAmounts)
        {
            _chosen = _isOther ? SponsorLink.Parse(OtherAmountBox.Text) : _chosen;

            foreach (var child in ChipRow.Children)
            {
                if (child is not Button chip) continue;

                var isChosen = !_isOther
                    && chip.DataContext is decimal value
                    && value == _chosen;

                chip.Tag = isChosen ? "chosen" : null;
            }

            OtherChip.Tag = _isOther ? "chosen" : null;
        }

        SponsorButton.Content = Label();
        SponsorButton.IsEnabled = !_showAmounts || _chosen is not null;
    }

    private string Label()
    {
        if (!_showAmounts) return "Open the sponsor page";
        if (_chosen is { } amount) return $"Sponsor {SponsorLink.Describe(amount)}";

        return SponsorLink.HasCents(OtherAmountBox.Text) ? "Whole amounts only" : "Enter an amount";
    }

    private void SponsorButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_template)) return;

        Open(_chosen is { } amount ? SponsorLink.For(_template, amount) : _template);
        Close();
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Hand the link to whatever the machine uses for links.
    /// <para>
    /// UseShellExecute, so this is the browser someone chose rather than an
    /// attempt to find one. Failures are swallowed: a machine with no default
    /// browser is a machine where nothing useful can happen here, and throwing
    /// out of a donate button would be an absurd way to lose the app.
    /// </para>
    /// </summary>
    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Nothing to do and nothing worth interrupting anyone over.
        }
    }
}
