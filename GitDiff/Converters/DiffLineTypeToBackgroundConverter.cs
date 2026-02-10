using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using GitDiff.Models;

namespace GitDiff.Converters;

public class DiffLineTypeToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DiffLineType type)
        {
            return type switch
            {
                DiffLineType.Added => new SolidColorBrush(Color.FromArgb(60, 76, 175, 80)),
                DiffLineType.Deleted => new SolidColorBrush(Color.FromArgb(60, 244, 67, 54)),
                DiffLineType.Hunk => new SolidColorBrush(Color.FromArgb(40, 33, 150, 243)),
                _ => Brushes.Transparent
            };
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
