using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using GitDiff.Models;

namespace GitDiff.Converters;

public class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ChangeStatus status)
        {
            return status switch
            {
                ChangeStatus.Added => new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                ChangeStatus.Modified => new SolidColorBrush(Color.FromRgb(33, 150, 243)),
                ChangeStatus.Deleted => new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                ChangeStatus.Renamed => new SolidColorBrush(Color.FromRgb(255, 152, 0)),
                ChangeStatus.Copied => new SolidColorBrush(Color.FromRgb(156, 39, 176)),
                _ => new SolidColorBrush(Colors.Gray)
            };
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
