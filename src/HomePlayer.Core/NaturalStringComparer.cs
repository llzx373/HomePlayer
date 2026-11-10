namespace HomePlayer.Core;

public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        left ??= "";
        right ??= "";
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
            {
                var leftEnd = DigitsEnd(left, leftIndex);
                var rightEnd = DigitsEnd(right, rightIndex);
                var leftNumber = left.AsSpan(leftIndex, leftEnd - leftIndex).TrimStart('0');
                var rightNumber = right.AsSpan(rightIndex, rightEnd - rightIndex).TrimStart('0');
                var length = leftNumber.Length.CompareTo(rightNumber.Length);
                if (length != 0)
                    return length;
                var number = leftNumber.CompareTo(rightNumber, StringComparison.OrdinalIgnoreCase);
                if (number != 0)
                    return number;
                leftIndex = leftEnd;
                rightIndex = rightEnd;
                continue;
            }

            var character = char.ToUpperInvariant(left[leftIndex]).CompareTo(char.ToUpperInvariant(right[rightIndex]));
            if (character != 0)
                return character;
            leftIndex++;
            rightIndex++;
        }

        return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
    }

    private static int DigitsEnd(string value, int start)
    {
        while (start < value.Length && char.IsDigit(value[start]))
            start++;
        return start;
    }
}
