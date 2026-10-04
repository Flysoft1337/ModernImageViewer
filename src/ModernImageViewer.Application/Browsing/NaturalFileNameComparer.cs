namespace ModernImageViewer.Application.Browsing;

public sealed class NaturalFileNameComparer : IComparer<string?>
{
    public static NaturalFileNameComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return StringComparer.OrdinalIgnoreCase.Compare(x, y);
        }

        int left = 0;
        int right = 0;
        while (left < x.Length && right < y.Length)
        {
            if (char.IsAsciiDigit(x[left]) && char.IsAsciiDigit(y[right]))
            {
                int leftStart = left;
                int rightStart = right;
                while (left < x.Length && char.IsAsciiDigit(x[left]))
                {
                    left++;
                }
                while (right < y.Length && char.IsAsciiDigit(y[right]))
                {
                    right++;
                }

                ReadOnlySpan<char> leftDigits = x.AsSpan(leftStart, left - leftStart).TrimStart('0');
                ReadOnlySpan<char> rightDigits = y.AsSpan(rightStart, right - rightStart).TrimStart('0');
                int result = leftDigits.Length.CompareTo(rightDigits.Length);
                if (result == 0)
                {
                    result = leftDigits.SequenceCompareTo(rightDigits);
                }
                if (result == 0)
                {
                    result = (left - leftStart).CompareTo(right - rightStart);
                }
                if (result != 0)
                {
                    return result;
                }
            }
            else
            {
                int result = char.ToUpperInvariant(x[left]).CompareTo(char.ToUpperInvariant(y[right]));
                if (result != 0)
                {
                    return result;
                }
                left++;
                right++;
            }
        }

        int remaining = (x.Length - left).CompareTo(y.Length - right);
        return remaining != 0 ? remaining : StringComparer.Ordinal.Compare(x, y);
    }
}
