using System.Globalization;
using System.Windows.Data;

namespace D2RExtractor.Converters;

/// <summary>
/// Gives one GridView column whatever width the fixed columns beside it do not use.
///
/// <para>
/// A GridView column width is a number, not a proportion, so a window narrower than the columns it
/// contains does not shrink them — it clips the rightmost one, and with the horizontal scrollbar
/// disabled there is nothing to scroll to reach it. That is how the Remove button lost its right
/// edge: every column had a fixed width, and the window could be dragged narrower than their sum.
/// </para>
///
/// <para>
/// Binding the one elastic column through this converter fixes the direction of give: the fixed
/// columns keep their widths and the flexible one absorbs the difference, so the buttons on the
/// right are never the thing that gets cut.
/// </para>
/// </summary>
[ValueConversion(typeof(double), typeof(double))]
public class RemainingWidthConverter : IValueConverter
{
    /// <summary>
    /// Width taken by everything else on the row: the other columns, the border, and room for a
    /// vertical scrollbar that may or may not be there. Set per use in XAML.
    /// </summary>
    public double Reserved { get; set; }

    /// <summary>
    /// How narrow this column may get before it stops shrinking. Below this the window clips again,
    /// which is why the windows using this also set a MinWidth that keeps the total above water —
    /// this is the second line of defence, not the first.
    /// </summary>
    public double Minimum { get; set; } = 80;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double available || double.IsNaN(available) || available <= 0)
            return Minimum;

        // ConvertParameter wins when given, so one converter instance can serve several columns.
        double reserved = parameter is string s
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)
            ? p
            : Reserved;

        return Math.Max(Minimum, available - reserved);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
