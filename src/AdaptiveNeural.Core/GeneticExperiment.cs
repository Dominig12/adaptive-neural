namespace AdaptiveNeural.Core;

/// <summary>
/// Outer-loop, seed-controlled genetic search over a small set of inheritable
/// traits. Each individual starts with a fresh network and develops through
/// local plasticity during its lifetime. Learned weights are NOT inherited.
/// This deliberately tests selection + development, not true neural evolution.
/// </summary>
public sealed record GeneticExperimentRequest(int Seed = 1337, int Population = 6,
    int Generations = 3, int StepsPerTrial = 450, int WorldTrials = 3);
public sealed record GeneticGenome(float LearningRate, float ReinforcementRate,
    float TargetActivity, float LeftMotorGain, float RightMotorGain, float ThrustMotorGain);
public sealed record GeneticGeneration(int Generation, float BestFitness,
    float MeanFitness, GeneticGenome BestGenome);
public sealed record GeneticExperimentResult(GeneticGeneration[] History,
    GeneticGenome Winner, float WinnerFitness, float BaselineFitness,
    int WorldTrials, int StepsPerTrial, string Interpretation);

public static class GeneticExperiment
{
    public static GeneticExperimentResult Run(GeneticExperimentRequest request)
    {
        int count = Math.Clamp(request.Population, 4, 16);
        int generations = Math.Clamp(request.Generations, 1, 10);
        int steps = Math.Clamp(request.StepsPerTrial, 180, 1200);
        int trials = Math.Clamp(request.WorldTrials, 1, 3);
        var random = new Random(request.Seed);
        var population = new List<GeneticGenome>(count) { DefaultGenome() };
        for (int i = 1; i < count; i++) population.Add(Mutate(DefaultGenome(), random, 0.7f));
        var history = new List<GeneticGeneration>(generations);
        float globalBestScore = float.NegativeInfinity;
        GeneticGenome globalBest = DefaultGenome();
        for (int generation = 0; generation < generations; generation++)
        {
            var ranked = population.Select(g => (Genome: g, Score: Evaluate(g, request.Seed, steps, trials)))
                .OrderByDescending(x => x.Score).ToArray();
            float best = ranked[0].Score;
            if (best > globalBestScore) { globalBestScore = best; globalBest = ranked[0].Genome; }
            history.Add(new GeneticGeneration(generation, best, (float)ranked.Average(x => x.Score), ranked[0].Genome));
            var next = new List<GeneticGenome>(count) { ranked[0].Genome, ranked[1].Genome };
            while (next.Count < count)
            {
                var a = ranked[random.Next(Math.Max(2, count / 2))].Genome;
                var b = ranked[random.Next(Math.Max(2, count / 2))].Genome;
                next.Add(Mutate(Cross(a, b, random), random, 0.20f));
            }
            population = next;
        }
        float baseline = Evaluate(DefaultGenome(), request.Seed, steps, trials);
        return new GeneticExperimentResult(history.ToArray(), globalBest, globalBestScore,
            baseline, trials, steps,
            "Fitness измеряет результат движения в заданных мирах, а не разум или обобщение. " +
            "Параметры наследуются, изменения весов в течение жизни — нет. " +
            "Проверяйте лучшие геномы на новых seed, иначе возможна подгонка к тренировочным картам.");
    }

    private static GeneticGenome DefaultGenome() => new(0.003f, 0.045f, 0.17f, 1f, 1f, 1f);

    private static GeneticGenome Cross(GeneticGenome a, GeneticGenome b, Random random) => new(
        random.Next(2) == 0 ? a.LearningRate : b.LearningRate,
        random.Next(2) == 0 ? a.ReinforcementRate : b.ReinforcementRate,
        random.Next(2) == 0 ? a.TargetActivity : b.TargetActivity,
        random.Next(2) == 0 ? a.LeftMotorGain : b.LeftMotorGain,
        random.Next(2) == 0 ? a.RightMotorGain : b.RightMotorGain,
        random.Next(2) == 0 ? a.ThrustMotorGain : b.ThrustMotorGain);

    private static GeneticGenome Mutate(GeneticGenome g, Random random, float magnitude)
    {
        float Change(float x, float range, float min, float max) =>
            Math.Clamp(x + ((float)random.NextDouble() * 2f - 1f) * magnitude * range, min, max);
        return new GeneticGenome(Change(g.LearningRate, 0.01f, 0f, 0.03f),
            Change(g.ReinforcementRate, 0.14f, 0f, 0.3f),
            Change(g.TargetActivity, 0.35f, 0.03f, 0.65f),
            Change(g.LeftMotorGain, 1.8f, 0.1f, 3f),
            Change(g.RightMotorGain, 1.8f, 0.1f, 3f),
            Change(g.ThrustMotorGain, 1.8f, 0.1f, 3f));
    }

    public static float Evaluate(GeneticGenome genome, int seed, int steps, int trials)
    {
        float sum = 0f;
        for (int trial = 0; trial < trials; trial++)
        {
            var net = new AdaptiveNetwork(new NetworkConfig
            {
                Seed = unchecked(seed + trial * 719), InitialNeurons = 96,
                MaximumNeurons = 240, MaximumEdges = 3600,
                LearningRate = genome.LearningRate, ReinforcementRate = genome.ReinforcementRate,
                TargetActivity = genome.TargetActivity,
                LeftMotorGain = genome.LeftMotorGain, RightMotorGain = genome.RightMotorGain,
                ThrustMotorGain = genome.ThrustMotorGain
            });
            var world = new ForagerWorld(unchecked(seed + trial * 941));
            if (trial > 0)
                world.ConfigureCourse(trial % 2 == 1 ? SpatialCourse.Upper : SpatialCourse.Lower);
            float initial = world.GoalDistance;
            for (int tick = 0; tick < steps; tick++)
            {
                net.Step(world.SampleSensors());
                WorldOutcome result = world.Step(net.ReadMotors());
                net.Reinforce((result.Food > 0 ? 1f : 0f) -
                    (result.HazardContact ? 0.08f : 0f) - (result.Collision ? 0.018f : 0f));
                net.Feed(world.Energy, result.Food);
            }
            // Distance shaping keeps a flat fitness landscape from overwhelming
            // selection before any individual has ever reached the goal.
            sum += (trial > 0 ? 12f * (initial - world.BestGoalDistance) : 0f) +
                   30f * world.FoodCollected + 2f * world.Energy -
                   0.03f * world.Collisions - 0.06f * world.HazardContacts;
        }
        return sum / trials;
    }
}
