namespace AdaptiveNeural.Core;

public sealed record RetentionExperimentRequest(int Seed = 1337, int Episodes = 20, int DelaySteps = 360);
public sealed record RetentionPoint(string Phase, float FamiliarResponse, float NovelResponse,
    float PatternSeparation, float FamiliarDriftFromBaseline, int StableConnections);
public sealed record RetentionExperimentResult(int Seed, int Episodes, int DelaySteps,
    RetentionPoint[] Results);

/// <summary>
/// Identically seeded, independently trained networks. Reads out responses to held-out
/// familiar/novel probes at baseline, immediately after training, after quiet and
/// after interfering stimuli. No desired labels or reward. Pattern separation and
/// drift are descriptive, not proof of recognition, generalization or recall.
/// </summary>
public static class RetentionExperiment
{
    private readonly record struct Signature(float[] Familiar, float[] Novel, float Separation,
        float FamiliarMean, float NovelMean);

    public static RetentionExperimentResult Run(RetentionExperimentRequest request)
    {
        int episodes = Math.Clamp(request.Episodes, 4, 48);
        int delay = Math.Clamp(request.DelaySteps, 60, 1800);
        var config = new NetworkConfig
        {
            Seed = request.Seed, InitialNeurons = 128, MaximumNeurons = 160,
            StructuralPlasticity = false, AdaptiveInterfaces = false, AdaptiveStructures = false,
            LearningRate = 0.007f, HomeostasisRate = 0.002f, MemoryConsolidation = true,
            MemoryActivationThreshold = 0.26f, ConsolidationRate = 0.32f
        };
        var baseline = new AdaptiveNetwork(config);
        var immediate = new AdaptiveNetwork(config);
        var rested = new AdaptiveNetwork(config);
        var distracted = new AdaptiveNetwork(config);
        var shuffled = new AdaptiveNetwork(config);
        float[] silent = new float[ForagerWorld.SensorChannels];
        float[] familiar = Pair(0, 1);
        float[] novel = Pair(4, 5);
        var random = new Random(unchecked(request.Seed ^ 0x5B4719));
        AdaptiveNetwork[] networks = [baseline, immediate, rested, distracted, shuffled];
        for (int t = 0; t < 80; t++) foreach (var n in networks) n.Step(silent);

        // Baseline is never trained. Training uses the same number and strength
        // of physical sensory signals in every experimental condition.
        Signature initial = Probe(baseline, familiar, novel, silent);
        for (int episode = 0; episode < episodes; episode++)
        {
            int a = random.Next(ForagerWorld.SensorChannels);
            int b = (a + 1 + random.Next(ForagerWorld.SensorChannels - 1)) % ForagerWorld.SensorChannels;
            float[] mixed = Pair(a, b);
            for (int t = 0; t < 14; t++)
            {
                immediate.Step(familiar); rested.Step(familiar); distracted.Step(familiar);
                shuffled.Step(mixed);
            }
            for (int t = 0; t < 52; t++)
            {
                immediate.Step(silent); rested.Step(silent); distracted.Step(silent);
                shuffled.Step(silent);
            }
        }
        // Matched delay lengths; only one group experiences interference.
        for (int t = 0; t < delay; t++)
        {
            rested.Step(silent);
            // Novel stimulus in short separated episodes, preserving overall
            // delay length but not equalizing the *absence* of sensory input.
            distracted.Step(t % 60 < 12 ? novel : silent);
            shuffled.Step(silent);
        }
        RetentionPoint[] points =
        [
            MakePoint("До обучения", initial, initial, baseline),
            MakePoint("Сразу после", Probe(immediate, familiar, novel, silent), initial, immediate),
            MakePoint("После тишины", Probe(rested, familiar, novel, silent), initial, rested),
            MakePoint("После помех", Probe(distracted, familiar, novel, silent), initial, distracted),
            MakePoint("Случайные пары", Probe(shuffled, familiar, novel, silent), initial, shuffled)
        ];
        return new RetentionExperimentResult(request.Seed, episodes, delay, points);
    }

    private static float[] Pair(int a, int b)
    {
        float[] pattern = new float[ForagerWorld.SensorChannels];
        pattern[a] = 3f;
        pattern[b] = 3f;
        return pattern;
    }

    private static Signature Probe(AdaptiveNetwork n, float[] familiar, float[] novel, float[] silent)
    {
        // All learning mechanisms are frozen for readout. The network still
        // evolves activity states, but cannot alter weights or thresholds.
        n.Config.LearningRate = 0f;
        n.Config.HomeostasisRate = 0f;
        n.Config.MemoryConsolidation = false;
        float[] first = Read(n, familiar, silent);
        float[] second = Read(n, novel, silent);
        float distance = 0f;
        for (int i = 0; i < first.Length; i++) distance += MathF.Abs(first[i] - second[i]);
        return new Signature(first, second, distance / first.Length,
            first.Average(), second.Average());
    }

    private static float[] Read(AdaptiveNetwork n, float[] pattern, float[] silent)
    {
        for (int t = 0; t < 90; t++) n.Step(silent);
        NetworkView topo = n.Snapshot(-1);
        int[] internalIds = topo.Nodes!.Where(v => v.Role == NeuronRole.Internal).Select(v => v.Id).ToArray();
        float[] mean = new float[internalIds.Length];
        for (int t = 0; t < 18; t++)
        {
            n.Step(pattern);
            if (t < 12) continue;
            float[] activity = n.Snapshot(topo.TopologyVersion).Activities;
            for (int k = 0; k < mean.Length; k++) mean[k] += activity[internalIds[k]] / 6f;
        }
        // Only internal cells contribute, not the directly stimulated receptors.
        return mean;
    }

    private static RetentionPoint MakePoint(string phase, Signature s, Signature initial, AdaptiveNetwork n)
    {
        float delta = 0f;
        for (int i = 0; i < s.Familiar.Length; i++) delta += MathF.Abs(s.Familiar[i] - initial.Familiar[i]);
        return new RetentionPoint(phase, MathF.Round(s.FamiliarMean, 5), MathF.Round(s.NovelMean, 5),
            MathF.Round(s.Separation, 5), MathF.Round(delta / s.Familiar.Length, 5),
            n.Snapshot(-1).Metrics.StableConnections);
    }
}
