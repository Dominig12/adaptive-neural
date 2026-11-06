using AdaptiveNeural.Core;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception($"FAILED: {message}");
    Console.WriteLine($"PASS: {message}");
}

var config = new NetworkConfig { Seed = 2468, InitialNeurons = 128, MaximumNeurons = 210 };
var a = new AdaptiveNetwork(config);
var b = new AdaptiveNetwork(config);
Check(a.NeuronCount == 128, "initial neuron budget");
Check(a.ConnectionCount > 0, "sparse initial connections");
Check(a.ConnectionCount < 128 * 24, "no dense quadratic network");
Check(a.Snapshot(-1).Nodes!.Length == 128, "initial topology snapshot");
Check(a.Snapshot(a.Snapshot(-1).TopologyVersion).Nodes is null, "topology delta omitted when version unchanged");

float[] sensors = [1.4f, 0.1f, 1f, 0f, 0.8f, 0f, 0f, 0.3f];
for (int step = 0; step < 1200; step++)
{
    if (step % 150 == 0)
    {
        a.Inject(0f, 0f, 3.5f);
        b.Inject(0f, 0f, 3.5f);
    }
    a.Step(sensors);
    b.Step(sensors);
    CheckTick(a);
}
void CheckTick(AdaptiveNetwork network)
{
    CheckQuiet(network.NeuronCount <= config.MaximumNeurons, "bounded neuron count");
    CheckQuiet(network.ConnectionCount <= config.MaximumEdges, "bounded edge count");
}
static void CheckQuiet(bool condition, string message)
{
    if (!condition) throw new Exception($"FAILED: {message}");
}
var snapshot = a.Snapshot(-1);
var snapshot2 = b.Snapshot(-1);
Check(snapshot.Metrics.Steps == 1200, "tick advancement");
Check(snapshot.Metrics.Neurons == snapshot2.Metrics.Neurons &&
      snapshot.Metrics.Connections == snapshot2.Metrics.Connections,
      "repeatable topology for same seed");
Check(snapshot.Activities.SequenceEqual(snapshot2.Activities), "repeatable activities for same seed");
Check(snapshot.Activities.All(v => float.IsFinite(v) && v >= 0f && v <= 1f), "finite bounded activation");
Check(snapshot.Edges!.All(e => e.From != e.To && float.IsFinite(e.Weight)), "valid synapses");

// Adaptive interfaces are virtual mappings to fixed physical sensors and motors.
// Extremely permissive thresholds are intentional here: verify that growth is possible,
// not that a particular real-world task has been learned.
var permissive = new NetworkConfig
{
    Seed = 724, InitialNeurons = 144, MaximumNeurons = 260, MaximumEdges = 3500,
    MaximumInputPorts = 14, MaximumOutputPorts = 8, MaximumStructures = 9,
    TargetActivity = 0.04f, GrowthThreshold = 0.005f,
    InterfaceGrowthThreshold = 0.005f, StructureGrowthThreshold = 0.005f
};
var evolving = new AdaptiveNetwork(permissive);
var evolvingTwin = new AdaptiveNetwork(permissive);
float[] highStimulus = [3f, 3f, 3f, 3f, 3f, 3f, 3f, 3f];
for (int i = 0; i < 1800; i++)
{
    evolving.Step(highStimulus);
    evolvingTwin.Step(highStimulus);
    if (i % 300 == 0)
    {
        CheckQuiet(evolving.NeuronCount <= permissive.MaximumNeurons, "growth neuron budget");
        CheckQuiet(evolving.InputPortCount <= permissive.MaximumInputPorts, "receptor budget");
        CheckQuiet(evolving.OutputPortCount <= permissive.MaximumOutputPorts, "effector budget");
        CheckQuiet(evolving.StructureCount <= permissive.MaximumStructures, "ensemble budget");
        CheckQuiet(evolving.ConnectionCount <= permissive.MaximumEdges, "synapse budget");
    }
}
Check(evolving.InputPortCount > 8, "activity-dependent virtual receptor genesis");
Check(evolving.OutputPortCount > 3, "activity-dependent virtual effector genesis");
Check(evolving.StructureCount > 4, "emergent structure partition and neurogenesis");
Check(evolving.InputPortCount == evolvingTwin.InputPortCount &&
      evolving.OutputPortCount == evolvingTwin.OutputPortCount &&
      evolving.StructureCount == evolvingTwin.StructureCount,
    "repeatable interface and structure evolution");
var evolvedView = evolving.Snapshot(-1);
Check(evolvedView.Inputs!.Length == evolving.InputPortCount &&
      evolvedView.Outputs!.Length == evolving.OutputPortCount, "port topology included in snapshot");
Check(evolvedView.Structures!.Length == evolving.StructureCount, "structure topology included in snapshot");
Check(evolvedView.Inputs!.All(v => evolvedView.Nodes![v.NeuronId].Role == NeuronRole.Sensor &&
      v.PhysicalChannel is >= 0 and < 8), "all virtual inputs map to valid physical sensors");
Check(evolvedView.Outputs!.All(v => evolvedView.Nodes![v.NeuronId].Role == NeuronRole.Motor &&
      v.PhysicalChannel is >= 0 and < 3), "all virtual outputs map to valid physical motors");
Check(evolving.ReadMotors().All(v => float.IsFinite(v) && v >= 0f && v <= 1f),
    "dynamic motor readout is bounded");
Check(evolving.Snapshot(evolvedView.TopologyVersion).Inputs is null,
    "unchanged interface topology omitted from incremental snapshot");

var noPorts = new AdaptiveNetwork(new NetworkConfig
{
    Seed = 724, InitialNeurons = 144, MaximumNeurons = 260, TargetActivity = 0.04f,
    GrowthThreshold = 0.005f, AdaptiveInterfaces = false, AdaptiveStructures = false
});
for (int i = 0; i < 900; i++) noPorts.Step(highStimulus);
Check(noPorts.InputPortCount == 8 && noPorts.OutputPortCount == 3 && noPorts.StructureCount == 4,
    "independent evolution toggles prevent port and structure births");

var disabled = new AdaptiveNetwork(new NetworkConfig { StructuralPlasticity = false });
int count = disabled.ConnectionCount;
for (int i = 0; i < 500; i++) disabled.Step(new float[8]);
Check(disabled.ConnectionCount == count && disabled.NeuronCount == 144, "structural adaptation toggle");

var engine = new SimulationEngine();
var initial = engine.Snapshot(-1);
engine.Apply(new ControlRequest(Action: "step"));
Check(engine.Snapshot(-1).Network.Metrics.Steps == 1, "manual step while paused");
engine.Tick();
Check(engine.Snapshot(-1).Network.Metrics.Steps == 1, "pause blocks automatic ticks");
engine.Apply(new ControlRequest(Action: "play", Speed: 4));
engine.Tick();
Check(engine.Snapshot(-1).Network.Metrics.Steps == 5, "speed multiplier");
engine.Spawn(new SpawnRequest(0.2f, 0.2f));
Check(engine.Snapshot(-1).World.Objects.Length == initial.World.Objects.Length + 1, "spawn resources");
engine.Apply(new ControlRequest(Action: "reset"));
Check(engine.Snapshot(-1).Network.Metrics.Steps == 0, "reset deterministic experiment");

Console.WriteLine($"\nAll checks passed. After 1200 ticks: {snapshot.Metrics.Neurons} neurons, {snapshot.Metrics.Connections} synapses, {snapshot.Metrics.Births} births, {snapshot.Metrics.PrunedConnections} pruned.");
