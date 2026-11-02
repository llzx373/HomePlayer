namespace HomePlayer.Core;

public static class DisplayRefreshRateOptions
{
    public static IReadOnlyList<int> Merge(IEnumerable<int> available, int systemReported) =>
        available.Append(systemReported)
            .Where(hertz => hertz > 0)
            .Distinct()
            .Order()
            .ToArray();
}
