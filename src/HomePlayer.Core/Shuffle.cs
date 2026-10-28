namespace HomePlayer.Core;

public static class Shuffle
{
    /// <summary>
    /// Fisher-Yates 洗牌，返回新列表，不改动输入。
    /// </summary>
    public static List<T> Order<T>(IReadOnlyList<T> items, Random? random = null)
    {
        var list = new List<T>(items);
        var rng = random ?? Random.Shared;
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }
}
