using System.Collections;
using System.Globalization;
using System.Windows.Data;
using GitDiff.Models;

namespace GitDiff.Converters;

public class CommitRoleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4) return string.Empty;

        var current = values[0] as CommitInfo;
        var baseCommit = values[1] as CommitInfo;
        var targetCommit = values[2] as CommitInfo;
        var commits = values[3] as IList;

        if (current == null) return string.Empty;
        if (baseCommit != null && current.Hash == baseCommit.Hash) return "Base";
        if (targetCommit != null && current.Hash == targetCommit.Hash) return "Target";

        if (baseCommit != null && targetCommit != null && commits != null)
        {
            int baseIdx = -1, targetIdx = -1, currentIdx = -1;
            for (var i = 0; i < commits.Count; i++)
            {
                if (commits[i] is not CommitInfo c) continue;
                if (c.Hash == baseCommit.Hash) baseIdx = i;
                if (c.Hash == targetCommit.Hash) targetIdx = i;
                if (c.Hash == current.Hash) currentIdx = i;
            }

            if (baseIdx >= 0 && targetIdx >= 0 && currentIdx >= 0)
            {
                var minIdx = Math.Min(baseIdx, targetIdx);
                var maxIdx = Math.Max(baseIdx, targetIdx);
                if (currentIdx > minIdx && currentIdx < maxIdx)
                    return "│";
            }
        }

        return string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
