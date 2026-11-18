using System.Globalization;

namespace MyCapture.Core.Export;

/// <summary>
/// Orders titles as File Explorer orders names: runs of ASCII digits compare by value ("Capture 9"
/// before "Capture 10"), and the text between them by culture, ignoring case. Digit runs of any
/// length compare without numeric conversion, so they never overflow. Equal values with different
/// leading zeros ("7" and "007") stay equal until the rest of the title is compared; the shorter run
/// then comes first.
/// </summary>
public sealed class NaturalTitleComparer(CultureInfo culture) : IComparer<string>
{
    private readonly CompareInfo _compare = culture.CompareInfo;

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        int leftIndex = 0, rightIndex = 0, zeros = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            bool leftDigit = char.IsAsciiDigit(left[leftIndex]), rightDigit = char.IsAsciiDigit(right[rightIndex]);
            int leftEnd = RunEnd(left, leftIndex, leftDigit), rightEnd = RunEnd(right, rightIndex, rightDigit);
            ReadOnlySpan<char> leftRun = left.AsSpan(leftIndex, leftEnd - leftIndex);
            ReadOnlySpan<char> rightRun = right.AsSpan(rightIndex, rightEnd - rightIndex);
            int result;
            if (leftDigit && rightDigit)
            {
                ReadOnlySpan<char> leftValue = leftRun.TrimStart('0'), rightValue = rightRun.TrimStart('0');
                // Without leading zeros, a longer run is a larger number; equal lengths compare digit by digit.
                result = leftValue.Length != rightValue.Length
                    ? leftValue.Length.CompareTo(rightValue.Length)
                    : leftValue.SequenceCompareTo(rightValue);
                if (result == 0 && zeros == 0) zeros = leftRun.Length.CompareTo(rightRun.Length);
            }
            else
            {
                // A number before text, as in Explorer; text against text by culture.
                result = leftDigit != rightDigit ? (leftDigit ? -1 : 1)
                    : _compare.Compare(leftRun, rightRun, CompareOptions.IgnoreCase);
            }
            if (result != 0) return Math.Sign(result);
            leftIndex = leftEnd;
            rightIndex = rightEnd;
        }
        int remaining = (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
        return remaining != 0 ? remaining : zeros;
    }

    private static int RunEnd(string text, int start, bool digits)
    {
        int end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]) == digits) end++;
        return end;
    }
}
