namespace AdaptiveNeural.Core;

public sealed class NetworkConfig
{
    public int Seed { get; set; } = 1337;
    public int InitialNeurons { get; set; } = 144;
    public int MaximumNeurons { get; set; } = 1200;
    public int MaximumEdges { get; set; } = 16000;
    public int MaximumOutgoing { get; set; } = 24;
    public float TargetActivity { get; set; } = 0.17f;
    public float LearningRate { get; set; } = 0.003f;
    public float HomeostasisRate { get; set; } = 0.005f;
    public float GrowthThreshold { get; set; } = 0.055f;
    public bool StructuralPlasticity { get; set; } = true;
    public bool AdaptiveInterfaces { get; set; } = true;
    public bool AdaptiveStructures { get; set; } = true;
    public int MaximumInputPorts { get; set; } = 20;
    public int MaximumOutputPorts { get; set; } = 10;
    public int MaximumStructures { get; set; } = 12;
    public float InterfaceGrowthThreshold { get; set; } = 0.075f;
    public float StructureGrowthThreshold { get; set; } = 0.085f;
    public bool MemoryConsolidation { get; set; } = true;
    public float MemoryActivationThreshold { get; set; } = 0.26f;
    public float ConsolidationRate { get; set; } = 0.30f;
    public float MemoryDecayRate { get; set; } = 0.00002f;
    // Metabolism is a separate structural budget, not the world's energy variable.
    public bool MetabolismEnabled { get; set; } = true;
    public float ResourceCapacity { get; set; } = 240f;
    public float InitialResource { get; set; } = 160f;
    public float ResourceSupplyRate { get; set; } = 0.16f;
    public bool ReinforcementEnabled { get; set; } = true;
    public float ReinforcementRate { get; set; } = 0.045f;
    // Genetic trials can evolve effector transduction without modifying the world.
    public float LeftMotorGain { get; set; } = 1f;
    public float RightMotorGain { get; set; } = 1f;
    public float ThrustMotorGain { get; set; } = 1f;

    public NetworkConfig Clone() => (NetworkConfig)MemberwiseClone();
}

public enum NeuronRole { Sensor, Internal, Motor }

public sealed record NeuronView(int Id, int Group, NeuronRole Role, float X, float Y, bool Dormant);
public sealed record EdgeView(int From, int To, float Weight);
public sealed record NetworkMetrics(int Neurons, int Connections, int Births, int NewConnections,
    int PrunedConnections, float MeanActivity, float MeanStress, long Steps,
    int InputPorts, int OutputPorts, int Structures, int InputBirths, int OutputBirths,
    int StructureBirths, int RetiredPorts, int StableConnections, int MemoryReactivations,
    float MeanConsolidation, float Resource, float ResourceCapacity, float Maintenance,
    float LastIntake, int StarvationTicks, int DormantNeurons, int AtrophyEvents,
    float GrowthExpenditure, float LastFeedback);
public sealed record NetworkView(int TopologyVersion, float[] Activities, NeuronView[]? Nodes,
    EdgeView[]? Edges, NetworkMetrics Metrics, InterfaceView[]? Inputs,
    InterfaceView[]? Outputs, StructureView[]? Structures, MemoryGroupView[] MemoryGroups);

public sealed partial class AdaptiveNetwork
{
    private const int SensorCount = ForagerWorld.SensorChannels;
    private const int MotorCount = 3;
    private const float MinWeight = -1.3f;
    private const float MaxWeight = 1.3f;

    private sealed class Neuron
    {
        public required int Id;
        public required int Group;
        public required NeuronRole Role;
        public required float X;
        public required float Y;
        public float Activity;
        public float NextActivity;
        public float RunningActivity = 0.17f;
        public float Stress;
        public float Threshold = 0.95f;
        public bool Dormant;
        public readonly List<Synapse> Outgoing = [];
    }

    private sealed class Synapse
    {
        public required int To;
        public float Weight;
        public float Utility = 0.025f;
        public long CreatedAt;
        public SynapticMemory Memory;
        public float Eligibility;
    }

    private sealed class Pulse
    {
        public float X, Y, Strength;
        public int TicksRemaining;
    }

    private readonly List<Neuron> _neurons = [];
    private readonly List<Pulse> _pulses = [];
    private readonly Random _random;
    private readonly float[] _incoming;
    private readonly int[] _sensorIds = new int[SensorCount];
    private readonly int[] _motorIds = new int[MotorCount];
    private int _connections;
    private int _births;
    private int _linksAdded;
    private int _linksPruned;
    private int _topologyVersion = 1;
    private float _meanActivity;
    private float _meanStress;
    private int _memoryReactivations;
    private int _stableConnections;
    private float _meanConsolidation;

    public NetworkConfig Config { get; }
    public long Steps { get; private set; }
    public int NeuronCount => _neurons.Count;
    public int ConnectionCount => _connections;

    public AdaptiveNetwork(NetworkConfig config)
    {
        Config = config.Clone();
        Config.MaximumNeurons = Math.Clamp(Config.MaximumNeurons, 32, 4000);
        Config.InitialNeurons = Math.Clamp(Config.InitialNeurons, SensorCount + MotorCount + 8, Config.MaximumNeurons);
        Config.MaximumEdges = Math.Clamp(Config.MaximumEdges, 256, 64000);
        Config.MaximumOutgoing = Math.Clamp(Config.MaximumOutgoing, 2, 64);
        Config.MaximumInputPorts = Math.Clamp(Config.MaximumInputPorts, SensorCount, 64);
        Config.MaximumOutputPorts = Math.Clamp(Config.MaximumOutputPorts, MotorCount, 32);
        Config.MaximumStructures = Math.Clamp(Config.MaximumStructures, 4, 32);
        Config.ResourceCapacity = Math.Clamp(float.IsFinite(Config.ResourceCapacity) ? Config.ResourceCapacity : 240f, 4f, 2000f);
        Config.InitialResource = Math.Clamp(float.IsFinite(Config.InitialResource) ? Config.InitialResource : 160f, 0f, Config.ResourceCapacity);
        Config.ResourceSupplyRate = Math.Clamp(float.IsFinite(Config.ResourceSupplyRate) ? Config.ResourceSupplyRate : 0.16f, 0f, 2f);
        Config.ReinforcementRate = Math.Clamp(float.IsFinite(Config.ReinforcementRate) ? Config.ReinforcementRate : 0.045f, 0f, 0.3f);
        Config.LeftMotorGain = Math.Clamp(float.IsFinite(Config.LeftMotorGain) ? Config.LeftMotorGain : 1f, 0.1f, 3f);
        Config.RightMotorGain = Math.Clamp(float.IsFinite(Config.RightMotorGain) ? Config.RightMotorGain : 1f, 0.1f, 3f);
        Config.ThrustMotorGain = Math.Clamp(float.IsFinite(Config.ThrustMotorGain) ? Config.ThrustMotorGain : 1f, 0.1f, 3f);
        _incoming = new float[Config.MaximumNeurons];
        _random = new Random(Config.Seed);
        Initialize();
        InitializeEvolution();
        InitializeMetabolism();
    }

    private void Initialize()
    {
        for (int i = 0; i < SensorCount; i++)
        {
            _sensorIds[i] = AddNeuron(0, NeuronRole.Sensor, -0.91f, -0.82f + i * 1.64f / (SensorCount - 1));
        }
        int hidden = Config.InitialNeurons - SensorCount - MotorCount;
        for (int i = 0; i < hidden; i++)
        {
            int group = 1 + (i % 4);
            float x = (float)(_random.NextDouble() * 1.14 - 0.57);
            float y = Math.Clamp((group - 2.5f) * 0.43f + (float)(_random.NextDouble() * 0.36 - 0.18), -0.88f, 0.88f);
            AddNeuron(group, NeuronRole.Internal, x, y);
        }
        for (int i = 0; i < MotorCount; i++)
        {
            _motorIds[i] = AddNeuron(5, NeuronRole.Motor, 0.91f, -0.60f + i * 0.60f);
        }

        // Cheap sparse scaffold: sensory -> associative, associative -> associative + motor.
        for (int i = 0; i < _neurons.Count; i++)
        {
            Neuron source = _neurons[i];
            if (source.Role == NeuronRole.Motor) continue;
            int edgesWanted = source.Role == NeuronRole.Sensor ? 7 : 5;
            for (int k = 0; k < edgesWanted; k++)
            {
                int candidate = ChooseNearbyInternal(source, 0.7f);
                TryConnect(source.Id, candidate, InitialWeight());
            }
            if (source.Role == NeuronRole.Internal && _random.NextDouble() < 0.65)
                TryConnect(source.Id, _motorIds[_random.Next(MotorCount)], InitialWeight());
        }
        // Ensure every motor receives multiple connections at initialization.
        for (int m = 0; m < MotorCount; m++)
            for (int j = 0; j < 10; j++)
                TryConnect(_random.Next(SensorCount, SensorCount + hidden), _motorIds[m], InitialWeight());
    }

    private float InitialWeight() => (float)(_random.NextDouble() * 1.4 - 0.25);

    private int AddNeuron(int group, NeuronRole role, float x, float y)
    {
        int id = _neurons.Count;
        _neurons.Add(new Neuron { Id = id, Group = group, Role = role, X = x, Y = y });
        return id;
    }

    private int ChooseNearbyInternal(Neuron source, float radius)
    {
        // Random sampling avoids a full all-pairs distance matrix.
        int best = -1;
        float bestScore = float.MaxValue;
        for (int trial = 0; trial < 18; trial++)
        {
            int id = _random.Next(SensorCount, _neurons.Count - MotorCount);
            Neuron candidate = _neurons[id];
            if (id == source.Id) continue;
            float dx = candidate.X - source.X;
            float dy = candidate.Y - source.Y;
            float score = dx * dx + dy * dy + (float)_random.NextDouble() * radius * radius;
            if (score < bestScore) { best = id; bestScore = score; }
        }
        return best;
    }

    private bool TryConnect(int from, int to, float weight)
    {
        if (from < 0 || to < 0 || from == to || _connections >= Config.MaximumEdges) return false;
        if (_neurons[from].Dormant || _neurons[to].Dormant) return false;
        Neuron source = _neurons[from];
        if (source.Outgoing.Count >= Config.MaximumOutgoing || source.Role == NeuronRole.Motor) return false;
        foreach (Synapse existing in source.Outgoing)
            if (existing.To == to) return false;
        if (!SpendForGrowth(SynapseGrowthCost)) return false;
        source.Outgoing.Add(new Synapse { To = to, Weight = Math.Clamp(weight, MinWeight, MaxWeight), CreatedAt = Steps });
        _connections++;
        _linksAdded++;
        _topologyVersion++;
        return true;
    }

    public void Inject(float x, float y, float strength = 2.4f)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(strength)) return;
        if (_pulses.Count == 12) _pulses.RemoveAt(0);
        _pulses.Add(new Pulse
        {
            X = Math.Clamp(x, -1f, 1f), Y = Math.Clamp(y, -1f, 1f),
            Strength = Math.Clamp(strength, 0f, 6f), TicksRemaining = 85
        });
    }

    public void Step(ReadOnlySpan<float> sensors)
    {
        if (sensors.Length != SensorCount) throw new ArgumentException("Unexpected physical sensor channel count.", nameof(sensors));
        Steps++;
        TickMetabolism();
        int count = _neurons.Count;
        Array.Clear(_incoming, 0, count);

        // Each synapse is visited only once; no dense N x N allocation.
        for (int i = 0; i < count; i++)
        {
            Neuron from = _neurons[i];
            if (from.Dormant) continue;
            for (int j = 0; j < from.Outgoing.Count; j++)
            {
                Synapse edge = from.Outgoing[j];
                _incoming[edge.To] += from.Activity * edge.Weight;
            }
        }
        DriveInputPorts(sensors);

        for (int p = _pulses.Count - 1; p >= 0; p--)
        {
            Pulse pulse = _pulses[p];
            float intensity = pulse.Strength * pulse.TicksRemaining / 85f;
            for (int i = 0; i < count; i++)
            {
                Neuron n = _neurons[i];
                float dx = n.X - pulse.X;
                float dy = n.Y - pulse.Y;
                float distance = dx * dx + dy * dy;
                if (distance < 0.085f) _incoming[i] += intensity * (1f - distance / 0.085f);
            }
            if (--pulse.TicksRemaining <= 0) _pulses.RemoveAt(p);
        }

        float activitySum = 0f, stressSum = 0f;
        float target = Math.Clamp(Config.TargetActivity, 0.03f, 0.65f);
        float homeo = Math.Clamp(Config.HomeostasisRate, 0f, 0.04f);
        for (int i = 0; i < count; i++)
        {
            Neuron n = _neurons[i];
            if (n.Dormant)
            {
                n.Activity = 0f;
                n.NextActivity = 0f;
                continue;
            }
            float drive = Math.Clamp((_incoming[i] - n.Threshold) * 3f, -14f, 14f);
            float response = 1f / (1f + MathF.Exp(-drive));
            n.NextActivity = n.Activity * 0.76f + response * 0.24f;
            // Exponential homeostasis works locally, not via a central reward signal.
            n.RunningActivity = n.RunningActivity * 0.985f + n.NextActivity * 0.015f;
            n.Stress = n.Stress * 0.94f + MathF.Abs(n.NextActivity - n.RunningActivity) * 0.06f;
            n.Threshold = Math.Clamp(n.Threshold + homeo * (n.RunningActivity - target), 0.25f, 3.4f);
            activitySum += n.NextActivity;
            stressSum += n.Stress;
        }

        float lr = Math.Clamp(Config.LearningRate, 0f, 0.03f);
        int stable = 0;
        float stabilitySum = 0f;
        for (int i = 0; i < count; i++)
        {
            Neuron from = _neurons[i];
            if (from.Dormant) continue;
            float source = from.NextActivity;
            for (int j = 0; j < from.Outgoing.Count; j++)
            {
                Synapse edge = from.Outgoing[j];
                float targetActivity = _neurons[edge.To].NextActivity;
                // Eligibility tracks recently coactive paths; later environmental feedback
                // is applied to this transient trace instead of all synapses equally.
                edge.Eligibility = Math.Clamp(edge.Eligibility * 0.93f +
                    MathF.Max(0f, source - 0.12f) * MathF.Max(0f, targetActivity - 0.12f) *
                    (from.Role == NeuronRole.Sensor ? 0.15f : _neurons[edge.To].Role == NeuronRole.Motor ? 1f : 0.32f), 0f, 3f);
                // Repeated distinct coactivation episodes strengthen a local stability trace.
                // Stable synapses are less plastic and resist pruning; they do not imply semantic memory.
                if (Config.MemoryConsolidation && edge.Memory.Observe(source, targetActivity, edge.Weight,
                    Steps, Config.MemoryActivationThreshold, Config.ConsolidationRate, Config.MemoryDecayRate))
                    _memoryReactivations++;
                float hebbian = source * targetActivity - 0.18f * source * (1f - targetActivity);
                float effectiveRate = lr * (1f - 0.75f * edge.Memory.Stability);
                edge.Weight = Math.Clamp(edge.Weight + effectiveRate * (hebbian - 0.025f * edge.Weight), MinWeight, MaxWeight);
                edge.Utility = edge.Utility * 0.99f + MathF.Abs(source * targetActivity * edge.Weight) * 0.01f;
                if (edge.Memory.IsStable) stable++;
                stabilitySum += edge.Memory.Stability;
            }
        }
        for (int i = 0; i < count; i++) _neurons[i].Activity = _neurons[i].NextActivity;
        int living = Math.Max(1, count - _dormantNeurons);
        _meanActivity = activitySum / living;
        _meanStress = stressSum / living;
        _stableConnections = stable;
        _meanConsolidation = _connections > 0 ? stabilitySum / _connections : 0f;
        ObserveGroupSignals(sensors);

        if (Config.StructuralPlasticity && Steps % 90 == 0) AdaptStructure();
        if (Config.StructuralPlasticity && Steps % 360 == 0) Prune();
        if (Config.StructuralPlasticity) RetireInactivePorts();
    }

    private void AdaptStructure()
    {
        // Local, activity-driven structural mutations. Group labels are spatial scaffolds,
        // not fixed cognitive roles. Growth is capped to prevent runaway resource use.
        // Snapshot the group IDs: splitting a group must not mutate this iteration.
        int[] groupIds = _structures.Keys.ToArray();
        bool evolvedInterfaces = false;
        bool formedStructure = false;
        foreach (int group in groupIds)
        {
            float pressure = 0f, average = 0f;
            int members = 0, focal = -1;
            float highest = 0f;
            for (int i = SensorCount; i < _neurons.Count; i++)
            {
                Neuron n = _neurons[i];
                if (n.Role != NeuronRole.Internal || n.Group != group || n.Dormant) continue;
                float need = n.Stress + MathF.Max(0f, n.RunningActivity - Config.TargetActivity);
                pressure += need;
                average += n.RunningActivity;
                members++;
                if (need > highest) { highest = need; focal = i; }
            }
            if (members == 0 || focal < 0) continue;
            pressure /= members;
            average /= members;
            if (pressure < Config.GrowthThreshold || average < Config.TargetActivity * 0.65f) continue;

            Neuron center = _neurons[focal];
            int closest = -1;
            float best = float.MaxValue;
            // Search a small randomized neighborhood for an active, unconnected target.
            for (int sample = 0; sample < Math.Min(48, _neurons.Count); sample++)
            {
                int id = _random.Next(_neurons.Count);
                if (id == focal || _neurons[id].Role == NeuronRole.Sensor || _neurons[id].Dormant) continue;
                Neuron candidate = _neurons[id];
                float dx = candidate.X - center.X, dy = candidate.Y - center.Y;
                float d = dx * dx + dy * dy;
                if (d >= 0.32f || d >= best || candidate.Activity < 0.06f) continue;
                if (center.Outgoing.Exists(e => e.To == id)) continue;
                closest = id;
                best = d;
            }
            if (closest >= 0) TryConnect(focal, closest, InitialWeight() * 0.65f);
            if (!evolvedInterfaces && pressure >= Config.InterfaceGrowthThreshold && Steps % 270 == 0)
            {
                EvolveInterfaces(focal, pressure);
                evolvedInterfaces = true;
            }
            if (!formedStructure && TryFormStructure(group, focal, pressure)) formedStructure = true;

            // Add a neuron only for persistent high pressure, at most once per group / 270 ticks.
            if (Steps % 270 != 0 || pressure < Config.GrowthThreshold * 1.4f ||
                _neurons.Count >= Config.MaximumNeurons || !SpendForGrowth(NeuronGrowthCost)) continue;
            float x = Math.Clamp(center.X + (float)(_random.NextDouble() - 0.5) * 0.12f, -0.84f, 0.84f);
            float y = Math.Clamp(center.Y + (float)(_random.NextDouble() - 0.5) * 0.12f, -0.94f, 0.94f);
            int born = AddNeuron(group, NeuronRole.Internal, x, y);
            _births++;
            _topologyVersion++;
            TryConnect(focal, born, 0.5f);
            if (closest >= 0) TryConnect(born, closest, 0.5f);
            else TryConnect(born, _motorIds[_random.Next(MotorCount)], 0.35f);
        }
    }

    private void Prune()
    {
        for (int i = 0; i < _neurons.Count; i++)
        {
            List<Synapse> edges = _neurons[i].Outgoing;
            if (edges.Count <= 2) continue;
            for (int j = edges.Count - 1; j >= 0 && edges.Count > 2; j--)
            {
                Synapse edge = edges[j];
                if (Steps - edge.CreatedAt < 360) continue;
                if (!edge.Memory.IsStable && (MathF.Abs(edge.Weight) < 0.035f || edge.Utility < 0.0018f))
                {
                    edges.RemoveAt(j);
                    _connections--;
                    _linksPruned++;
                    _topologyVersion++;
                }
            }
        }
    }

    public float[] ReadMotors()
    {
        return ReadOutputPorts();
    }

    public NetworkView Snapshot(int knownTopologyVersion)
    {
        float[] activities = new float[_neurons.Count];
        for (int i = 0; i < activities.Length; i++) activities[i] = MathF.Round(_neurons[i].Activity, 4);
        NeuronView[]? nodes = null;
        EdgeView[]? edges = null;
        if (knownTopologyVersion != _topologyVersion)
        {
            nodes = new NeuronView[_neurons.Count];
            var topologyEdges = new List<EdgeView>(_connections);
            for (int i = 0; i < _neurons.Count; i++)
            {
                Neuron n = _neurons[i];
                nodes[i] = new NeuronView(n.Id, n.Group, n.Role, n.X, n.Y, n.Dormant);
                foreach (Synapse edge in n.Outgoing)
                    topologyEdges.Add(new EdgeView(i, edge.To, MathF.Round(edge.Weight, 3)));
            }
            edges = topologyEdges.ToArray();
        }
        return new NetworkView(_topologyVersion, activities, nodes, edges,
            new NetworkMetrics(_neurons.Count, _connections, _births, _linksAdded,
                _linksPruned, MathF.Round(_meanActivity, 4), MathF.Round(_meanStress, 4), Steps,
                _inputs.Count, _outputs.Count, _structures.Count, _inputBirths, _outputBirths,
                _structureBirths, _portsRetired, _stableConnections, _memoryReactivations,
                MathF.Round(_meanConsolidation, 4), MathF.Round(_resource, 3),
                MathF.Round(Config.ResourceCapacity, 3), MathF.Round(_maintenance, 4),
                MathF.Round(_lastIntake, 4), _starvationTicks, _dormantNeurons,
                _atrophyEvents, MathF.Round(_growthExpenditure, 3), MathF.Round(_lastFeedback, 4)),
            nodes is null ? null : SnapshotPorts(_inputs),
            nodes is null ? null : SnapshotPorts(_outputs),
            nodes is null ? null : SnapshotStructures(), SnapshotMemoryGroups());
    }
}
