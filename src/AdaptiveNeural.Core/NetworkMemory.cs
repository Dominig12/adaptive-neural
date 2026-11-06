namespace AdaptiveNeural.Core;

/// <summary>
/// Local, episode-gated stability trace. Repeated above-threshold coactivity
/// separated by a quiet interval consolidates a synapse; continuous firing
/// contributes only one episode. No target labels or reward are used.
/// </summary>
public struct SynapticMemory
{
    public const float StableThreshold = 0.50f;
    public float Stability { get; private set; }
    public int Episodes { get; private set; }
    public long LastEpisode { get; private set; }
    public bool IsStable => Stability >= StableThreshold;
    private bool _coactive;

    /// <returns>True only if a new coactivity episode was observed.</returns>
    public bool Observe(float pre, float post, float weight, long step,
        float activationThreshold, float rate, float decayRate)
    {
        float threshold = Math.Clamp(activationThreshold, 0.1f, 0.8f);
        float learning = Math.Clamp(rate, 0f, 1f);
        float forgetting = Math.Clamp(decayRate, 0f, 0.003f);
        bool active = weight > 0.08f && pre >= threshold && post >= threshold;
        bool episode = false;
        if (active && !_coactive && (LastEpisode == 0 || step - LastEpisode >= 8))
        {
            Episodes = Math.Min(Episodes + 1, 1000000);
            LastEpisode = step;
            // First coincident event is a candidate, not established memory.
            if (Episodes >= 2) Stability += (1f - Stability) * learning;
            episode = true;
        }
        if (active) _coactive = true;
        else if (pre < threshold * 0.8f || post < threshold * 0.8f || weight <= 0.08f)
            _coactive = false;
        if (!active) Stability = MathF.Max(0f, Stability - forgetting);
        return episode;
    }
}

public sealed record MemoryGroupView(int GroupId, int StableConnections, int Connections,
    float MeanStability, int PreferredSensor, float Selectivity);

public sealed partial class AdaptiveNetwork
{
    private sealed class GroupSignalHistory
    {
        public readonly float[] Correlations = new float[SensorCount];
    }

    private readonly Dictionary<int, GroupSignalHistory> _groupSignalHistory = [];
    // Group IDs are bounded by MaximumStructures (<= 32) plus the initial scaffold.
    // Reused scratch buffers keep per-tick group aggregation allocation-free.
    private readonly float[] _groupActivitySum = new float[64];
    private readonly int[] _groupActivityCount = new int[64];

    // A descriptive statistic, not an explicitly assigned functional specialization.
    // Tracks sensory co-occurrence with each group's mean activity (EMA).
    private void ObserveGroupSignals(ReadOnlySpan<float> sensors)
    {
        Array.Clear(_groupActivitySum);
        Array.Clear(_groupActivityCount);
        foreach (Neuron n in _neurons)
        {
            if (n.Role != NeuronRole.Internal || (uint)n.Group >= _groupActivityCount.Length) continue;
            _groupActivitySum[n.Group] += n.Activity;
            _groupActivityCount[n.Group]++;
        }
        foreach (int id in _structures.Keys)
        {
            int count = _groupActivityCount[id];
            if (count == 0) continue;
            if (!_groupSignalHistory.TryGetValue(id, out GroupSignalHistory? history))
                _groupSignalHistory[id] = history = new GroupSignalHistory();
            float activity = _groupActivitySum[id] / count;
            for (int k = 0; k < SensorCount; k++)
            {
                float input = float.IsFinite(sensors[k]) ? Math.Clamp(sensors[k] / 3f, 0f, 1f) : 0f;
                history.Correlations[k] = history.Correlations[k] * 0.997f + input * activity * 0.003f;
            }
        }
    }

    private MemoryGroupView[] SnapshotMemoryGroups()
    {
        var counts = new Dictionary<int, (int Edges, int Stable, float Sum)>();
        foreach (Neuron from in _neurons)
        {
            if (from.Role != NeuronRole.Internal) continue;
            var current = counts.GetValueOrDefault(from.Group);
            foreach (Synapse edge in from.Outgoing)
            {
                current.Edges++;
                if (edge.Memory.IsStable) current.Stable++;
                current.Sum += edge.Memory.Stability;
            }
            counts[from.Group] = current;
        }
        var groups = new List<MemoryGroupView>(_structures.Count);
        foreach (int id in _structures.Keys.Order())
        {
            var v = counts.GetValueOrDefault(id);
            int preferred = -1;
            float best = 0f, second = 0f;
            if (_groupSignalHistory.TryGetValue(id, out GroupSignalHistory? history))
            {
                for (int i = 0; i < SensorCount; i++)
                {
                    float score = history.Correlations[i];
                    if (score > best) { second = best; best = score; preferred = i; }
                    else if (score > second) second = score;
                }
            }
            float selectivity = best <= 0.0001f ? 0f : (best - second) / best;
            groups.Add(new MemoryGroupView(id, v.Stable, v.Edges,
                v.Edges > 0 ? MathF.Round(v.Sum / v.Edges, 4) : 0f,
                preferred, MathF.Round(selectivity, 3)));
        }
        return groups.ToArray();
    }
}
