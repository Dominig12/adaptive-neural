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
