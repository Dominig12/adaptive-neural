namespace AdaptiveNeural.Core;

public sealed record SpatialExperimentRequest(int Seed = 1337, int Episodes = 8,
    int EpisodeSteps = 360, int EvaluationSteps = 480);
public sealed record RoutePoint(float X, float Y);
public sealed record SpatialTrial(string Phase, string Course, int FoodCollected,
    int Collisions, float ClosestGoalDistance, float FinalGoalDistance,
    float DistanceTravelled, float Energy, int Neurons, int StableConnections,
    RoutePoint[] Trajectory, WorldObjectView[] Objects);
public sealed record SpatialExperimentResult(int Seed, int Episodes, int EpisodeSteps,
    int EvaluationSteps, SpatialTrial[] Trials, SpatialTrial[] TrainingHistory);

/// <summary>
/// Closed-loop route transfer experiment. Independent networks with the same seed
/// experience the same initial route. After training, synaptic adaptation is frozen
/// for readout. The training-disabled control separates parameter adaptation from
/// an unmodified network's ordinary recurrent dynamics, but it does not match every
/// sensory input (the agents can take different paths). Results are descriptive.
/// </summary>
public static class SpatialExperiment
{
    public static SpatialExperimentResult Run(SpatialExperimentRequest request)
    {
        int episodes = Math.Clamp(request.Episodes, 2, 16);
        int trainSteps = Math.Clamp(request.EpisodeSteps, 90, 720);
        int testSteps = Math.Clamp(request.EvaluationSteps, 90, 900);
        var config = new NetworkConfig
        {
            Seed = request.Seed, InitialNeurons = 128, MaximumNeurons = 210,
            MaximumEdges = 3200, MaximumOutgoing = 20,
            LearningRate = 0.006f, HomeostasisRate = 0.003f,
            MemoryConsolidation = true, StructuralPlasticity = true,
            AdaptiveInterfaces = true, AdaptiveStructures = true
        };
        var trainedUpper = new AdaptiveNetwork(config);
        var trainedLower = new AdaptiveNetwork(config);
        var frozenControl = new AdaptiveNetwork(config);
        Freeze(frozenControl);
        var training = new List<SpatialTrial>(episodes);
        for (int episode = 0; episode < episodes; episode++)
        {
            training.Add(Trial(trainedUpper, request.Seed, SpatialCourse.Upper,
                trainSteps, $"Обучение {episode + 1}"));
            // Matched training runs permit evaluation on two different maps
            // without a mutable network clone or readout affecting learning.
            Trial(trainedLower, request.Seed, SpatialCourse.Upper, trainSteps, "training");
            Trial(frozenControl, request.Seed, SpatialCourse.Upper, trainSteps, "control");
        }
        Freeze(trainedUpper);
        Freeze(trainedLower);
        var freshUpper = new AdaptiveNetwork(config);
        var freshLower = new AdaptiveNetwork(config);
        Freeze(freshUpper);
        Freeze(freshLower);
        SpatialTrial[] trials =
        [
            Trial(freshUpper, request.Seed, SpatialCourse.Upper, testSteps, "Без опыта · исходный маршрут"),
            Trial(trainedUpper, request.Seed, SpatialCourse.Upper, testSteps, "После опыта · исходный маршрут"),
            Trial(freshLower, request.Seed, SpatialCourse.Lower, testSteps, "Без опыта · изменённый маршрут"),
            Trial(trainedLower, request.Seed, SpatialCourse.Lower, testSteps, "После опыта · изменённый маршрут"),
            Trial(frozenControl, request.Seed, SpatialCourse.Lower, testSteps, "Контроль без пластичности · изменённый маршрут")
        ];
        return new SpatialExperimentResult(request.Seed, episodes, trainSteps, testSteps,
            trials, training.ToArray());
    }

    private static void Freeze(AdaptiveNetwork network)
    {
        network.Config.LearningRate = 0f;
        network.Config.HomeostasisRate = 0f;
        network.Config.MemoryConsolidation = false;
        network.Config.StructuralPlasticity = false;
        network.Config.AdaptiveInterfaces = false;
        network.Config.AdaptiveStructures = false;
    }

    private static SpatialTrial Trial(AdaptiveNetwork net, int seed, SpatialCourse course,
        int ticks, string name)
    {
        var world = new ForagerWorld(seed, populate: false);
        world.ConfigureCourse(course);
        WorldObjectView[] objects = world.Snapshot().Objects;
        var points = new List<RoutePoint>(ticks / 12 + 2) { new(world.X, world.Y) };
        for (int step = 0; step < ticks; step++)
        {
            net.Step(world.SampleSensors());
            world.Step(net.ReadMotors());
            if (step % 12 == 11 || step == ticks - 1)
                points.Add(new RoutePoint(MathF.Round(world.X, 4), MathF.Round(world.Y, 4)));
            if (world.FoodCollected > 0) break;
        }
        return new SpatialTrial(name, world.Course, world.FoodCollected, world.Collisions,
            MathF.Round(world.BestGoalDistance, 4), MathF.Round(world.GoalDistance, 4),
            MathF.Round(world.DistanceTravelled, 4), MathF.Round(world.Energy, 4),
            net.NeuronCount, net.Snapshot(int.MaxValue).Metrics.StableConnections,
            points.ToArray(), objects);
    }
}
