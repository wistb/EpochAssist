namespace EpochAssist.Core;

/// <summary>
/// The host's one order of features: each after its Needs and RunsAfter, ties in registration order, so a list that
/// already respects them comes out unchanged. A need that isn't a feature, or a cycle, is a problem for the features
/// involved; features on a cycle go last, in registration order.
/// </summary>
internal static class UpdateOrder
{
    public static (List<Feature> Order, Dictionary<Feature, string> Problems) Sort(IReadOnlyList<Feature> features)
    {
        int count = features.Count;
        var problems = new Dictionary<Feature, string>();
        var index = new Dictionary<string, int>();
        for (int i = 0; i < count; i++) index.TryAdd(features[i].Id, i);

        // after[i]: the registered features i runs after; unknown RunsAfter ids are left out (dev features are absent
        // in release builds), unknown needs are a problem.
        var after = new List<int>[count];
        for (int i = 0; i < count; i++)
        {
            after[i] = new List<int>();
            foreach (string need in features[i].Needs)
            {
                if (index.TryGetValue(need, out int j)) after[i].Add(j);
                else problems.TryAdd(features[i], $"needs '{need}', which isn't a feature");
            }
            foreach (string id in features[i].RunsAfter)
            {
                if (index.TryGetValue(id, out int j)) after[i].Add(j);
            }
        }

        // Kahn's algorithm, always taking the earliest-registered feature that is ready. When none is ready, the
        // features left that sit on a cycle are set aside and the rest go on.
        var order = new List<Feature>(count);
        var cycled = new List<int>();
        var placed = new bool[count];
        int left = count;
        while (left > 0)
        {
            int ready = -1;
            for (int i = 0; i < count && ready < 0; i++)
            {
                if (!placed[i] && after[i].TrueForAll(j => placed[j])) ready = i;
            }
            if (ready >= 0)
            {
                placed[ready] = true;
                order.Add(features[ready]);
                left--;
                continue;
            }
            int found = cycled.Count;
            for (int i = 0; i < count; i++)
            {
                if (placed[i] || Cycle(i, after, placed) is not { } path) continue;
                cycled.Add(i);
                problems[features[i]] = "its needs form a cycle: " + string.Join(" → ", path.Select(j => features[j].Id));
            }
            // Can't happen (nothing ready means a cycle is left), but never loop forever: the rest go in as registered.
            if (cycled.Count == found)
            {
                for (int i = 0; i < count; i++)
                {
                    if (!placed[i]) cycled.Add(i);
                }
            }
            foreach (int i in cycled)
            {
                if (placed[i]) continue;
                placed[i] = true;
                left--;
            }
        }
        cycled.Sort();
        order.AddRange(cycled.Select(i => features[i]));
        return (order, problems);
    }

    // A shortest path from the feature through what it runs after, back to itself, among those not yet placed; null
    // when it isn't on a cycle.
    private static List<int>? Cycle(int start, List<int>[] after, bool[] placed)
    {
        var from = new Dictionary<int, int>();
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int at = queue.Dequeue();
            foreach (int next in after[at])
            {
                if (placed[next]) continue;
                if (next == start)
                {
                    var path = new List<int> { start };
                    for (int step = at; step != start; step = from[step]) path.Insert(1, step);
                    path.Add(start);
                    return path;
                }
                if (from.ContainsKey(next)) continue;
                from[next] = at;
                queue.Enqueue(next);
            }
        }
        return null;
    }
}
