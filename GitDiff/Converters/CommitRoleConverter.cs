using System.Globalization;
using System.Windows.Data;
using GitDiff.Models;

namespace GitDiff.Converters;

public class CommitRoleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3) return string.Empty;

        var current = values[0] as CommitInfo;
        var baseCommit = values[1] as CommitInfo;
        var targetCommit = values[2] as CommitInfo;

        if (current == null) return string.Empty;
        if (current == baseCommit) return "Base";
        if (current == targetCommit) return "Target";
        return string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
