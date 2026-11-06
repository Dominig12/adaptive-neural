namespace AdaptiveNeural.Core;

// Physical world channels are fixed. The network can independently develop and retire
// virtual receptors (derived features) and motor projections (parallel readouts).
public sealed record InterfaceView(int NeuronId, int PhysicalChannel, int? SecondaryChannel,
    float Mix, float Gain, bool Primary);
public sealed record StructureView(int Id, int? ParentId, int Members, long CreatedAt);

public sealed partial class AdaptiveNetwork
{
    private sealed class AdaptivePort
    {
        public required int NeuronId;
        public required int Channel;
        public int? Secondary;
        public float Mix;
        public float Gain = 1f;
        public bool Primary;
        public long CreatedAt;
        public float Usage;
    }

    private readonly List<AdaptivePort> _inputs = [];
    private readonly List<AdaptivePort> _outputs = [];
    private readonly Dictionary<int, (int? ParentId, long CreatedAt)> _structures = [];
    private int _nextStructureId = 6;
    private int _inputBirths;
    private int _outputBirths;
    private int _structureBirths;
    private int _portsRetired;

    public int InputPortCount => _inputs.Count;
    public int OutputPortCount => _outputs.Count;
    public int StructureCount => _structures.Count;

    private void InitializeEvolution()
    {
        for (int i = 0; i < SensorCount; i++)
            _inputs.Add(new AdaptivePort { NeuronId = _sensorIds[i], Channel = i, Primary = true });
        for (int i = 0; i < MotorCount; i++)
            _outputs.Add(new AdaptivePort { NeuronId = _motorIds[i], Channel = i, Primary = true });
        for (int group = 1; group <= 4; group++)
            _structures.Add(group, (null, 0));
    }

    private void DriveInputPorts(ReadOnlySpan<float> sensors)
    {
        foreach (AdaptivePort port in _inputs)
        {
            float a = float.IsFinite(sensors[port.Channel]) ? Math.Clamp(sensors[port.Channel], 0f, 3f) : 0f;
            float b = port.Secondary is int secondary && float.IsFinite(sensors[secondary])
                ? Math.Clamp(sensors[secondary], 0f, 3f) : 0f;
            float signal = (a * (1f - port.Mix) + b * port.Mix) * port.Gain;
            _incoming[port.NeuronId] += signal;
            port.Usage = port.Usage * 0.995f + signal * 0.005f;
        }
    }

    private float[] ReadOutputPorts()
    {
        Span<float> sums = stackalloc float[MotorCount];
        Span<float> gains = stackalloc float[MotorCount];
        foreach (AdaptivePort port in _outputs)
        {
            float activation = _neurons[port.NeuronId].Activity;
            sums[port.Channel] += activation * port.Gain;
            gains[port.Channel] += port.Gain;
            port.Usage = port.Usage * 0.995f + activation * 0.005f;
        }
        float[] result = new float[MotorCount];
        for (int i = 0; i < MotorCount; i++)
            result[i] = gains[i] > 0f ? Math.Clamp(sums[i] / gains[i], 0f, 1f) : 0f;
        return result;
    }

    // Called only on the structural adaptation cadence, not on every simulation tick.
    // A sensory/motor port is a neuron with an interface mapping, not a new physical sensor.
    private void EvolveInterfaces(int focalId, float pressure)
    {
        if (!Config.AdaptiveInterfaces || pressure < Config.InterfaceGrowthThreshold) return;
        if (Steps % 270 != 0) return;

        if (_inputs.Count < Config.MaximumInputPorts && _neurons.Count < Config.MaximumNeurons)
        {
            AdaptivePort parent = _inputs[_random.Next(SensorCount)];
            for (int i = 0; i < SensorCount; i++)
                if (_inputs[i].Usage > parent.Usage) parent = _inputs[i];
            int second = (parent.Channel + 1 + _random.Next(SensorCount - 1)) % SensorCount;
            Neuron anchor = _neurons[parent.NeuronId];
            int id = AddNeuron(0, NeuronRole.Sensor,
                Math.Clamp(anchor.X + 0.035f, -0.98f, 0.98f),
                Math.Clamp(anchor.Y + (float)(_random.NextDouble() - 0.5) * 0.12f, -0.95f, 0.95f));
            _inputs.Add(new AdaptivePort
            {
                NeuronId = id, Channel = parent.Channel, Secondary = second,
                Mix = 0.25f + (float)_random.NextDouble() * 0.5f,
                Gain = 0.85f, CreatedAt = Steps
            });
            _births++;
            _inputBirths++;
            _topologyVersion++;
            TryConnect(id, focalId, 0.7f);
            int candidate = FindRelatedInternal(focalId);
            if (candidate >= 0) TryConnect(id, candidate, 0.4f);
        }

        if (_outputs.Count >= Config.MaximumOutputPorts || _neurons.Count >= Config.MaximumNeurons) return;
        int feeder = FindAvailableFeeder(focalId);
        if (feeder < 0) return;
        int channel = _random.Next(MotorCount);
        Neuron anchorMotor = _neurons[_motorIds[channel]];
        int motorId = AddNeuron(5, NeuronRole.Motor,
            Math.Clamp(anchorMotor.X - 0.035f, -0.98f, 0.98f),
            Math.Clamp(anchorMotor.Y + (float)(_random.NextDouble() - 0.5) * 0.12f, -0.95f, 0.95f));
        _outputs.Add(new AdaptivePort
        {
            NeuronId = motorId, Channel = channel, Gain = 0.6f, CreatedAt = Steps
        });
        _births++;
        _outputBirths++;
        _topologyVersion++;
        TryConnect(feeder, motorId, 0.85f);
        int another = FindAvailableFeeder(focalId);
        if (another >= 0 && another != feeder) TryConnect(another, motorId, 0.45f);
    }

    private int FindAvailableFeeder(int focalId)
    {
        if (_neurons[focalId].Outgoing.Count < Config.MaximumOutgoing) return focalId;
        for (int i = 0; i < 48; i++)
        {
            int id = _random.Next(_neurons.Count);
            if (_neurons[id].Role == NeuronRole.Internal &&
                _neurons[id].Outgoing.Count < Config.MaximumOutgoing) return id;
        }
        return -1;
    }

    private int FindRelatedInternal(int focalId)
    {
        Neuron focal = _neurons[focalId];
        int bestId = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < Math.Min(64, _neurons.Count); i++)
        {
            int id = _random.Next(_neurons.Count);
            Neuron n = _neurons[id];
            if (id == focalId || n.Role != NeuronRole.Internal) continue;
            float distance = (n.X - focal.X) * (n.X - focal.X) +
                (n.Y - focal.Y) * (n.Y - focal.Y) + MathF.Abs(n.Activity - focal.Activity) * 0.1f;
            if (distance < bestDistance) { bestId = id; bestDistance = distance; }
        }
        return bestId;
    }

    private bool TryFormStructure(int group, int focalId, float pressure)
    {
        if (!Config.AdaptiveStructures || Steps % 540 != 0 ||
            pressure < Config.StructureGrowthThreshold ||
            _structures.Count >= Config.MaximumStructures ||
            _neurons.Count + 2 > Config.MaximumNeurons ||
            _connections + 8 > Config.MaximumEdges) return false;

        Neuron focal = _neurons[focalId];
        List<(int Id, float Score)> neighbors = [];
        int groupSize = 0;
        foreach (Neuron n in _neurons)
        {
            if (n.Role != NeuronRole.Internal || n.Group != group) continue;
            groupSize++;
            if (n.Id == focalId) continue;
            float distance = (n.X - focal.X) * (n.X - focal.X) +
                (n.Y - focal.Y) * (n.Y - focal.Y);
            float score = distance + 0.12f * MathF.Abs(n.Activity - focal.Activity);
            neighbors.Add((n.Id, score));
        }
        if (groupSize < 12 || neighbors.Count < 4) return false;
        neighbors.Sort((a, b) => a.Score.CompareTo(b.Score));

        int newGroup = _nextStructureId++;
        _structures.Add(newGroup, (group, Steps));
        focal.Group = newGroup;
        for (int j = 0; j < 4; j++) _neurons[neighbors[j].Id].Group = newGroup;
        for (int j = 0; j < 2; j++)
        {
            int born = AddNeuron(newGroup, NeuronRole.Internal,
                Math.Clamp(focal.X + (float)(_random.NextDouble() - 0.5f) * 0.14f, -0.86f, 0.86f),
                Math.Clamp(focal.Y + (float)(_random.NextDouble() - 0.5f) * 0.14f, -0.93f, 0.93f));
            _births++;
            TryConnect(focalId, born, 0.65f);
            TryConnect(born, neighbors[j].Id, 0.6f);
            TryConnect(neighbors[j].Id, born, 0.35f);
            int peer = neighbors[j + 2].Id;
            TryConnect(born, peer, 0.55f);
        }
        _structureBirths++;
        _topologyVersion++;
        return true;
    }

    private void RetireInactivePorts()
    {
        if (!Config.AdaptiveInterfaces || Steps % 900 != 0) return;
        for (int i = _inputs.Count - 1; i >= SensorCount; i--)
        {
            AdaptivePort port = _inputs[i];
            if (Steps - port.CreatedAt < 1800 || port.Usage > 0.02f) continue;
            _inputs.RemoveAt(i);
            Neuron n = _neurons[port.NeuronId];
            n.Role = NeuronRole.Internal;
            n.Group = 1;
            _portsRetired++;
            _topologyVersion++;
        }
        for (int i = _outputs.Count - 1; i >= MotorCount; i--)
        {
            AdaptivePort port = _outputs[i];
            if (Steps - port.CreatedAt < 1800 || port.Usage > 0.02f) continue;
            _outputs.RemoveAt(i);
            Neuron n = _neurons[port.NeuronId];
            n.Role = NeuronRole.Internal;
            n.Group = 4;
            TryConnect(n.Id, _motorIds[port.Channel], 0.4f);
            _portsRetired++;
            _topologyVersion++;
        }
    }

    private InterfaceView[] SnapshotPorts(List<AdaptivePort> ports) =>
        ports.Select(p => new InterfaceView(p.NeuronId, p.Channel, p.Secondary,
            MathF.Round(p.Mix, 3), MathF.Round(p.Gain, 3), p.Primary)).ToArray();

    private StructureView[] SnapshotStructures()
    {
        var counts = new Dictionary<int, int>();
        foreach (Neuron n in _neurons)
            if (n.Role == NeuronRole.Internal)
                counts[n.Group] = counts.GetValueOrDefault(n.Group) + 1;
        return _structures.Select(p => new StructureView(p.Key, p.Value.ParentId,
            counts.GetValueOrDefault(p.Key), p.Value.CreatedAt)).ToArray();
    }
}
