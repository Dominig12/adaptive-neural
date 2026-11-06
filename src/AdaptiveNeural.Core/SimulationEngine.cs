namespace AdaptiveNeural.Core;

public sealed record SimulationView(bool Paused, int Speed, NetworkView Network, WorldView World);
public sealed record ControlRequest(string? Action = null, int? Speed = null, int? InitialNeurons = null,
    int? MaximumNeurons = null, float? TargetActivity = null, float? LearningRate = null,
    float? GrowthThreshold = null, bool? StructuralPlasticity = null,
    bool? AdaptiveInterfaces = null, bool? AdaptiveStructures = null,
    int? MaximumInputPorts = null, int? MaximumOutputPorts = null,
    int? MaximumStructures = null, float? InterfaceGrowthThreshold = null,
    float? StructureGrowthThreshold = null, bool? MemoryConsolidation = null,
    float? MemoryActivationThreshold = null, float? ConsolidationRate = null);
public sealed record StimulusRequest(float X, float Y, float Strength = 2.4f);
public sealed record SpawnRequest(float X, float Y, bool Hazard = false);

public sealed class SimulationEngine
{
    private readonly object _gate = new();
    private NetworkConfig _config = new();
    private AdaptiveNetwork _network;
    private ForagerWorld _world;
    private bool _paused;
    private int _speed = 1;

    public SimulationEngine()
    {
        _network = new AdaptiveNetwork(_config);
        _world = new ForagerWorld(_config.Seed);
    }

    private void TickUnsafe()
    {
        _network.Step(_world.SampleSensors());
        _world.Step(_network.ReadMotors());
    }

    public void Tick()
    {
        lock (_gate)
        {
            if (_paused) return;
            for (int i = 0; i < _speed; i++) TickUnsafe();
        }
    }

    public void Apply(ControlRequest request)
    {
        lock (_gate)
        {
            if (request.Speed.HasValue) _speed = Math.Clamp(request.Speed.Value, 1, 8);
            if (request.TargetActivity.HasValue && float.IsFinite(request.TargetActivity.Value))
                _config.TargetActivity = _network.Config.TargetActivity = Math.Clamp(request.TargetActivity.Value, 0.03f, 0.65f);
            if (request.LearningRate.HasValue && float.IsFinite(request.LearningRate.Value))
                _config.LearningRate = _network.Config.LearningRate = Math.Clamp(request.LearningRate.Value, 0f, 0.03f);
            if (request.GrowthThreshold.HasValue && float.IsFinite(request.GrowthThreshold.Value))
                _config.GrowthThreshold = _network.Config.GrowthThreshold = Math.Clamp(request.GrowthThreshold.Value, 0.01f, 0.25f);
            if (request.StructuralPlasticity.HasValue)
                _config.StructuralPlasticity = _network.Config.StructuralPlasticity = request.StructuralPlasticity.Value;
            if (request.AdaptiveInterfaces.HasValue)
                _config.AdaptiveInterfaces = _network.Config.AdaptiveInterfaces = request.AdaptiveInterfaces.Value;
            if (request.AdaptiveStructures.HasValue)
                _config.AdaptiveStructures = _network.Config.AdaptiveStructures = request.AdaptiveStructures.Value;
            if (request.MaximumInputPorts.HasValue)
                _config.MaximumInputPorts = _network.Config.MaximumInputPorts = Math.Clamp(request.MaximumInputPorts.Value, 8, 64);
            if (request.MaximumOutputPorts.HasValue)
                _config.MaximumOutputPorts = _network.Config.MaximumOutputPorts = Math.Clamp(request.MaximumOutputPorts.Value, 3, 32);
            if (request.MaximumStructures.HasValue)
                _config.MaximumStructures = _network.Config.MaximumStructures = Math.Clamp(request.MaximumStructures.Value, 4, 32);
            if (request.InterfaceGrowthThreshold.HasValue && float.IsFinite(request.InterfaceGrowthThreshold.Value))
                _config.InterfaceGrowthThreshold = _network.Config.InterfaceGrowthThreshold = Math.Clamp(request.InterfaceGrowthThreshold.Value, 0.005f, 0.3f);
            if (request.StructureGrowthThreshold.HasValue && float.IsFinite(request.StructureGrowthThreshold.Value))
                _config.StructureGrowthThreshold = _network.Config.StructureGrowthThreshold = Math.Clamp(request.StructureGrowthThreshold.Value, 0.005f, 0.3f);
            if (request.MemoryConsolidation.HasValue)
                _config.MemoryConsolidation = _network.Config.MemoryConsolidation = request.MemoryConsolidation.Value;
            if (request.MemoryActivationThreshold.HasValue && float.IsFinite(request.MemoryActivationThreshold.Value))
                _config.MemoryActivationThreshold = _network.Config.MemoryActivationThreshold =
                    Math.Clamp(request.MemoryActivationThreshold.Value, 0.1f, 0.8f);
            if (request.ConsolidationRate.HasValue && float.IsFinite(request.ConsolidationRate.Value))
                _config.ConsolidationRate = _network.Config.ConsolidationRate =
                    Math.Clamp(request.ConsolidationRate.Value, 0f, 1f);
            if (request.InitialNeurons.HasValue)
                _config.InitialNeurons = Math.Clamp(request.InitialNeurons.Value, 20, 1500);
            if (request.MaximumNeurons.HasValue)
                _config.MaximumNeurons = Math.Clamp(request.MaximumNeurons.Value, 32, 4000);

            switch (request.Action?.ToLowerInvariant())
            {
                case "pause": _paused = true; break;
                case "play": _paused = false; break;
                case "step": TickUnsafe(); _paused = true; break;
                case "reset":
                    _network = new AdaptiveNetwork(_config);
                    _world = new ForagerWorld(_config.Seed);
                    _paused = true;
                    break;
            }
        }
    }

    public void Inject(StimulusRequest request)
    {
        lock (_gate) _network.Inject(request.X, request.Y, request.Strength);
    }

    public void Spawn(SpawnRequest request)
    {
        lock (_gate) _world.Spawn(request.X, request.Y, request.Hazard);
    }

    public SimulationView Snapshot(int topologyVersion)
    {
        lock (_gate)
            return new SimulationView(_paused, _speed, _network.Snapshot(topologyVersion), _world.Snapshot());
    }
}
