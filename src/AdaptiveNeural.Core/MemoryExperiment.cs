namespace AdaptiveNeural.Core;

public sealed record MemoryExperimentRequest(int Seed = 1337, int Episodes = 24);
public sealed record MemoryExperimentPoint(int Episode, int RepeatedStable,
    int ShuffledStable, float RepeatedMean, float ShuffledMean);
public sealed record MemoryExperimentResult(int Seed, int Episodes, MemoryExperimentPoint[] Series,
    float RepeatedProbeResponse, float ShuffledProbeResponse, int RepeatedReactivations,
    int ShuffledReactivations);

/// <summary>
/// Controlled descriptive study: same seed and input energy in both conditions.
/// One system sees a repeated sensory pair, the other shuffled pairs.
/// Structural plasticity is disabled to isolate synaptic consolidation.
/// This experiment reports observations; it does not assert intelligence or recall.
/// </summary>
public static class MemoryExperiment
{
    public static MemoryExperimentResult Run(MemoryExperimentRequest request)
    {
        int episodes = Math.Clamp(request.Episodes, 4, 48);
        int seed = request.Seed;
        var config = new NetworkConfig
        {
            Seed = seed, InitialNeurons = 128, MaximumNeurons = 160,
            StructuralPlasticity = false, LearningRate = 0.006f,
            TargetActivity = 0.15f, MemoryConsolidation = true,
            MemoryActivationThreshold = 0.26f, ConsolidationRate = 0.30f
        };
        var repeated = new AdaptiveNetwork(config);
        var shuffled = new AdaptiveNetwork(config);
        var rng = new Random(unchecked(seed ^ 0x2BD43A));
        var series = new MemoryExperimentPoint[episodes];
        float[] quiet = new float[8];
        float[] familiar = new float[8];
        familiar[0] = 3f;
        familiar[1] = 3f;

        for (int i = 0; i < 70; i++) { repeated.Step(quiet); shuffled.Step(quiet); }
        for (int episode = 0; episode < episodes; episode++)
        {
            float[] different = new float[8];
            int a = rng.Next(8);
            int b = (a + 1 + rng.Next(7)) % 8;
            different[a] = 3f;
            different[b] = 3f;
            for (int i = 0; i < 14; i++) { repeated.Step(familiar); shuffled.Step(different); }
            for (int i = 0; i < 64; i++) { repeated.Step(quiet); shuffled.Step(quiet); }
            NetworkMetrics m1 = repeated.Snapshot(-1).Metrics;
            NetworkMetrics m2 = shuffled.Snapshot(-1).Metrics;
            series[episode] = new MemoryExperimentPoint(episode + 1, m1.StableConnections,
                m2.StableConnections, m1.MeanConsolidation, m2.MeanConsolidation);
        }

        // Probe after matching training durations. Freeze all weight/homeostasis/memory updates;
        // measure response amplitude relative to the quiet state for the familiar input.
        float r = Probe(repeated, familiar, quiet);
        float s = Probe(shuffled, familiar, quiet);
        return new MemoryExperimentResult(seed, episodes, series, r, s,
            repeated.Snapshot(-1).Metrics.MemoryReactivations,
            shuffled.Snapshot(-1).Metrics.MemoryReactivations);
    }

    private static float Probe(AdaptiveNetwork network, float[] pattern, float[] quiet)
    {
        network.Config.LearningRate = 0f;
        network.Config.HomeostasisRate = 0f;
        network.Config.MemoryConsolidation = false;
        for (int i = 0; i < 70; i++) network.Step(quiet);
        float[] before = network.Snapshot(-1).Activities;
        for (int i = 0; i < 14; i++) network.Step(pattern);
        float[] after = network.Snapshot(-1).Activities;
        float sum = 0f;
        for (int i = 0; i < before.Length; i++)
            sum += MathF.Abs(after[i] - before[i]);
        return MathF.Round(sum / before.Length, 5);
    }
}
