namespace AdaptiveNeural.Core;

public sealed record WorldObjectView(float X, float Y, bool Hazard);
public sealed record WorldView(float X, float Y, float Heading, float Energy, int FoodCollected,
    float[] Sensors, float[] Motors, WorldObjectView[] Objects);

/// <summary>
/// Closed-loop test environment. It is deliberately not a reward-based training system:
/// survival or food-seeking behavior is not guaranteed to emerge from stabilization alone.
/// </summary>
public sealed class ForagerWorld
{
    private sealed class WorldObject
    {
        public float X, Y;
        public bool Hazard;
    }

    private readonly Random _random;
    private readonly List<WorldObject> _objects = [];
    private readonly float[] _sensors = new float[8];
    private float[] _motors = new float[3];
    private long _steps;
    public float X { get; private set; } = 0f;
    public float Y { get; private set; } = 0f;
    public float Heading { get; private set; } = 0f;
    public float Energy { get; private set; } = 0.68f;
    public int FoodCollected { get; private set; }

    public ForagerWorld(int seed)
    {
        _random = new Random(seed ^ 0x27B05A);
        for (int i = 0; i < 8; i++) SpawnRandom(false);
        for (int i = 0; i < 5; i++) SpawnRandom(true);
    }

    private void SpawnRandom(bool hazard) => _objects.Add(new WorldObject
    {
        X = (float)(_random.NextDouble() * 1.7 - 0.85),
        Y = (float)(_random.NextDouble() * 1.7 - 0.85),
        Hazard = hazard
    });

    public void Spawn(float x, float y, bool hazard)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return;
        if (_objects.Count >= 40) _objects.RemoveAt(0);
        _objects.Add(new WorldObject
        {
            X = Math.Clamp(x, -0.93f, 0.93f),
            Y = Math.Clamp(y, -0.93f, 0.93f),
            Hazard = hazard
        });
    }

    public ReadOnlySpan<float> SampleSensors()
    {
        Array.Clear(_sensors);
        foreach (WorldObject item in _objects)
        {
            float dx = item.X - X, dy = item.Y - Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance > 0.9f) continue;
            float signal = MathF.Pow(1f - distance / 0.9f, 1.4f) * 2.7f;
            float angle = MathF.Atan2(dy, dx) - Heading;
            angle = MathF.Atan2(MathF.Sin(angle), MathF.Cos(angle));
            int channel;
            if (MathF.Abs(angle) < MathF.PI / 4f) channel = 0;
            else if (angle > MathF.PI * 3f / 4f || angle < -MathF.PI * 3f / 4f) channel = 3;
            else if (angle > 0) channel = 1;
            else channel = 2;
            if (item.Hazard)
            {
                if (channel == 3) continue; // No dedicated rear hazard receptor in this minimal setup.
                channel += 4;
            }
            _sensors[channel] = Math.Clamp(_sensors[channel] + signal, 0f, 3f);
        }
        _sensors[7] = Math.Clamp((0.7f - Energy) * 3.2f, 0f, 2f);
        return _sensors;
    }

    public void Step(float[] motors, float dt = 1f / 30f)
    {
        if (motors.Length != 3) throw new ArgumentException("Expected three motor channels.", nameof(motors));
        _steps++;
        _motors = motors;
        float left = Math.Clamp(motors[0], 0f, 1f);
        float right = Math.Clamp(motors[1], 0f, 1f);
        float thrust = Math.Clamp(motors[2], 0f, 1f);
        // Weak exploratory drift is part of the environment baseline, not learned behavior.
        Heading += ((right - left) * 2.8f + 0.11f * MathF.Sin(_steps * 0.011f)) * dt;
        float speed = 0.045f + 0.28f * thrust;
        X += MathF.Cos(Heading) * speed * dt;
        Y += MathF.Sin(Heading) * speed * dt;
        if (X < -0.95f || X > 0.95f || Y < -0.95f || Y > 0.95f)
        {
            X = Math.Clamp(X, -0.95f, 0.95f);
            Y = Math.Clamp(Y, -0.95f, 0.95f);
            Heading += MathF.PI * 0.76f;
        }
        Energy = Math.Clamp(Energy - dt * (0.004f + speed * 0.012f), 0f, 1f);
        for (int i = 0; i < _objects.Count; i++)
        {
            WorldObject item = _objects[i];
            float dx = X - item.X, dy = Y - item.Y;
            if (dx * dx + dy * dy > 0.009f) continue;
            if (item.Hazard)
            {
                Energy = Math.Clamp(Energy - dt * 0.25f, 0f, 1f);
            }
            else
            {
                Energy = Math.Clamp(Energy + 0.19f, 0f, 1f);
                FoodCollected++;
                item.X = (float)(_random.NextDouble() * 1.7 - 0.85);
                item.Y = (float)(_random.NextDouble() * 1.7 - 0.85);
            }
        }
    }

    public WorldView Snapshot() => new(X, Y, Heading, Energy, FoodCollected,
        (float[])_sensors.Clone(), (float[])_motors.Clone(),
        _objects.Select(o => new WorldObjectView(o.X, o.Y, o.Hazard)).ToArray());
}
