using System.Globalization;

namespace HomePlayer.Core;

public static class PlaybackRates
{
    public static readonly double[] All = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];

    public static double Clamp(double rate) => Math.Clamp(rate, 0.5, 2.0);

    public static double Next(double rate)
    {
        for (var i = 0; i < All.Length; i++)
        {
            if (Math.Abs(All[i] - rate) < 0.001)
                return All[(i + 1) % All.Length];
        }

        foreach (var item in All)
        {
            if (item > rate)
                return item;
        }

        return All[0];
    }

    public static string Label(double rate)
    {
        var value = Clamp(rate);
        var text = Math.Abs(value % 1) < 0.001
            ? value.ToString("0.0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
        return text + "×";
    }
}
