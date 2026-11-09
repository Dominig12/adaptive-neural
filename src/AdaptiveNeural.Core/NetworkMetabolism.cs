namespace AdaptiveNeural.Core;

/// <summary>
/// A bounded structural reserve. This is an experimental metabolic analogy, not
/// a conservation-of-energy simulation or evidence of biological equivalence.
/// Only structural changes cost growth tokens; each active node/link/port/group
/// consumes maintenance on every simulated tick.
/// </summary>
public sealed partial class AdaptiveNetwork
{
    private const float NeuronGrowthCost = 2.0f;
    private const float SynapseGrowthCost = 0.16f;
    private const float PortGrowthCost = 1.25f;
    private const float StructureGrowthCost = 5f;
    private float _resource;
    private float _maintenance;
    private float _lastIntake;
    private float _growthExpenditure;
    private float _lastFeedback;
    private int _starvationTicks;
    private int _dormantNeurons;
    private int _atrophyEvents;
    private bool _metabolismReady;

    public float Resource => _resource;
    public int DormantNeuronCount => _dormantNeurons;

    private void InitializeMetabolism()
    {
        _resource = Config.InitialResource;
        _metabolismReady = true;
    }

    /// <summary>Transfer environmental nourishment to the neural structural reserve.
    /// Pass a normalized world energy value (0..1); food can supply an additional bonus.
    /// No budget is created when metabolism is disabled.
    /// </summary>
    public void Feed(float bodilyEnergy, int foodUnits = 0)
    {
        if (!Config.MetabolismEnabled) return;
        if (!float.IsFinite(bodilyEnergy)) return;
        float supply = Math.Clamp(bodilyEnergy, 0f, 1f) * Config.ResourceSupplyRate +
                       Math.Clamp(foodUnits, 0, 10) * 6f;
        float actual = Math.Clamp(supply, 0f, MathF.Max(0f, Config.ResourceCapacity - _resource));
        _resource += actual;
        _lastIntake += actual;
    }

    private bool SpendForGrowth(float cost)
    {
        if (!_metabolismReady || !Config.MetabolismEnabled) return true;
        // The growth reserve protects the ability to maintain existing structures.
        if (_resource < cost + MathF.Min(12f, Config.ResourceCapacity * 0.08f)) return false;
        _resource -= cost;
        _growthExpenditure += cost;
        return true;
    }

    private void TickMetabolism()
    {
        if (!Config.MetabolismEnabled) return;
        _lastIntake = 0f;
        int living = _neurons.Count - _dormantNeurons;
        _maintenance = living * 0.00022f + _connections * 0.000035f +
                       (_inputs.Count + _outputs.Count) * 0.00015f + _structures.Count * 0.001f;
        if (_resource >= _maintenance)
        {
            _resource -= _maintenance;
            _starvationTicks = 0;
            // Dormant internal cells may become available again if reserves recover.
            if (_dormantNeurons > 0 && _resource > Config.ResourceCapacity * 0.65f && Steps % 180 == 0)
                TryRevive();
            return;
        }
        _resource = 0f;
        _starvationTicks++;
        if (_starvationTicks >= 90 && _starvationTicks % 90 == 0)
            AtrophySynapses(5);
        if (_starvationTicks >= 270 && _starvationTicks % 270 == 0)
        {
            if (!RetireOneDerivedPort()) AtrophyInternalNeuron();
            _atrophyEvents++;
        }
    }

    private void AtrophySynapses(int limit)
    {
        // Lowest utility goes first. Consolidated traces are comparatively protected,
        // but severe starvation can still dismantle them (no permanent free memory).
        var candidates = new List<(int From, Synapse Edge, float Score)>();
        for (int i = 0; i < _neurons.Count; i++)
        {
            Neuron from = _neurons[i];
            if (from.Dormant || from.Outgoing.Count <= 1) continue;
            foreach (Synapse edge in from.Outgoing)
                candidates.Add((i, edge, edge.Utility + 0.05f * edge.Memory.Stability));
        }
        candidates.Sort((a, b) => a.Score.CompareTo(b.Score));
        int removed = 0;
        foreach (var c in candidates)
        {
            if (removed == limit) break;
            var source = _neurons[c.From];
            if (source.Outgoing.Count <= 1 || !source.Outgoing.Remove(c.Edge)) continue;
            removed++;
            _connections--;
            _linksPruned++;
            _topologyVersion++;
        }
        if (removed > 0) _atrophyEvents++;
    }

    private void AtrophyInternalNeuron()
    {
        Neuron? victim = null;
        foreach (Neuron n in _neurons)
        {
            if (n.Role != NeuronRole.Internal || n.Dormant) continue;
            if (victim is null || n.RunningActivity < victim.RunningActivity) victim = n;
        }
        if (victim is null) return;
        victim.Dormant = true;
        victim.Activity = victim.NextActivity = 0f;
        _dormantNeurons++;
        foreach (Neuron n in _neurons)
        {
            if (n.Id == victim.Id)
            {
                _connections -= n.Outgoing.Count;
                _linksPruned += n.Outgoing.Count;
                n.Outgoing.Clear();
            }
            else
            {
                int removed = n.Outgoing.RemoveAll(e => e.To == victim.Id);
                _connections -= removed;
                _linksPruned += removed;
            }
        }
        _topologyVersion++;
    }

    private void TryRevive()
    {
        Neuron? n = _neurons.FirstOrDefault(n => n.Dormant && n.Role == NeuronRole.Internal);
        if (n is null || !SpendForGrowth(NeuronGrowthCost)) return;
        n.Dormant = false;
        _dormantNeurons--;
        n.RunningActivity = Config.TargetActivity;
        n.Threshold = 0.95f;
        int source = FindRelatedInternal(n.Id);
        if (source >= 0) TryConnect(source, n.Id, 0.5f);
        TryConnect(n.Id, _motorIds[_random.Next(MotorCount)], 0.4f);
        _topologyVersion++;
    }

    /// <summary>Three-factor plasticity: a recent coactivation eligibility trace
    /// is multiplied by a signed environmental outcome. This is local credit
    /// assignment, not backpropagation or a learned value function.
    /// </summary>
    public void Reinforce(float outcome)
    {
        _lastFeedback = float.IsFinite(outcome) ? Math.Clamp(outcome, -1f, 1f) : 0f;
        if (!Config.ReinforcementEnabled || _lastFeedback == 0f) return;
        float lr = Config.ReinforcementRate * _lastFeedback;
        foreach (Neuron n in _neurons)
        {
            if (n.Dormant) continue;
            foreach (Synapse e in n.Outgoing)
            {
                // Consolidated connections are still trainable, but change slower.
                e.Weight = Math.Clamp(e.Weight + lr * e.Eligibility *
                    (1f - 0.6f * e.Memory.Stability), MinWeight, MaxWeight);
                e.Eligibility *= 0.72f;
            }
        }
    }
}
