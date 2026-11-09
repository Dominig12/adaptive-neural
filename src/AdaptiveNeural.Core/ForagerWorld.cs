namespace AdaptiveNeural.Core;

public sealed record WorldObjectView(float X, float Y, bool Hazard, string Kind, float Radius, bool Visible);
public sealed record WorldView(float X, float Y, float Heading, float Energy, int FoodCollected,
    float[] Sensors, float[] Motors, WorldObjectView[] Objects,
    float VisionRange, float EffectiveVisionRange, float FieldOfViewDegrees,
    float Daylight, float AmbientTemperature, float Exposure, int VisibleObjects, int Collisions,
    string Course, float GoalDistance, float BestGoalDistance, float DistanceTravelled, int HazardContacts);

public enum SpatialCourse { Upper, Lower }

public sealed record WorldOutcome(int Food, bool HazardContact, bool Collision,
    float EnergyChange);


/// <summary>
/// Deterministic continuous 2D environment. Only objects within a vision cone and
/// unobstructed by solid rocks contribute to visual sensors. Metabolic/thermal
/// sensors remain available in the dark. No reward or ideal movement is supplied
/// to the neural network.
/// </summary>
public sealed class ForagerWorld
{
    // Three bearing bins per kind (food / danger / refuge) + wall, hunger, thermal strain.
    // Changing this also changes AdaptiveNetwork's physical sensory interface count.
    public const int SensorChannels = 12;
    private const float AgentRadius = 0.032f;
    private const float TwoPi = 2f * MathF.PI;

    private sealed class WorldObject
    {
        public float X, Y;
        public required string Kind;
        public float Radius;
    }

    private readonly Random _random;
    private readonly List<WorldObject> _objects = [];
    private readonly float[] _sensors = new float[SensorChannels];
    private readonly float[] _motors = new float[3];
    private long _steps;
    private int _collisions;
    private int _hazardContacts;
    private int _visibleObjects;
    private bool _fixedCourse;
    private string _course = "sandbox";
    private float _bestGoalDistance;
    private float _distanceTravelled;
    private const float GoalX = 0.76f, GoalY = 0f;
    public string Course => _course;
    public float GoalDistance => _fixedCourse ? MathF.Sqrt((GoalX - X) * (GoalX - X) + (GoalY - Y) * (GoalY - Y)) : 0f;
    public float BestGoalDistance => _bestGoalDistance;
    public float DistanceTravelled => _distanceTravelled;

    public float X { get; private set; }
    public float Y { get; private set; }
    public float Heading { get; private set; }
    public float Energy { get; private set; } = 0.68f;
    public int FoodCollected { get; private set; }
    public float VisionRange { get; private set; } = 0.95f;
    public float FieldOfViewDegrees { get; private set; } = 140f;
    public float Daylight { get; private set; } = 0.85f;
    public float AmbientTemperature { get; private set; }
    public float Exposure { get; private set; }
    public int Collisions => _collisions;
    public int HazardContacts => _hazardContacts;
    public float EffectiveVisionRange => VisionRange * (0.28f + Daylight * 0.72f);

    public ForagerWorld(int seed, bool populate = true)
    {
        _random = new Random(unchecked(seed ^ 0x27B05A));
        // Never cover the origin: the agent always starts outside solid obstacles.
        if (populate)
        {
            for (int i = 0; i < 8; i++) SpawnRandom("food");
            for (int i = 0; i < 5; i++) SpawnRandom("hazard");
            for (int i = 0; i < 6; i++) SpawnRandom("rock");
            for (int i = 0; i < 3; i++) SpawnRandom("shelter");
        }
        UpdateClimate();
        SampleSensors();
    }

    /// <summary>
    /// Repeatable navigation course: a barrier has a traversable opening above or below
    /// the centre. Maps are otherwise identical. Reconfiguring a course resets the
    /// physical agent but does not touch any neural network attached to the world.
    /// </summary>
    public void ConfigureCourse(SpatialCourse layout)
    {
        _objects.Clear();
        _fixedCourse = true;
        _course = layout == SpatialCourse.Upper ? "upper" : "lower";
        X = -0.77f; Y = 0f; Heading = 0f;
        Energy = 0.85f; FoodCollected = 0;
        _steps = 0; _collisions = 0; _hazardContacts = 0; _distanceTravelled = 0f;
        _bestGoalDistance = GoalDistance;
        Array.Clear(_motors);
        float opening = layout == SpatialCourse.Upper ? -0.39f : 0.39f;
        for (int i = -6; i <= 6; i++)
        {
            float y = i * 0.13f;
            if (MathF.Abs(y - opening) <= 0.20f) continue;
            _objects.Add(new WorldObject { X = -0.04f, Y = y, Kind = "rock", Radius = RadiusFor("rock") });
        }
        // A landmark near the opening is visible to the agent. It gives the
        // environment a reusable cue without supplying the correct action.
        _objects.Add(new WorldObject { X = -0.31f, Y = opening, Kind = "shelter", Radius = RadiusFor("shelter") });
        _objects.Add(new WorldObject { X = GoalX, Y = GoalY, Kind = "food", Radius = RadiusFor("food") });
        UpdateClimate();
        SampleSensors();
    }

    public void ConfigureVision(float? range = null, float? angle = null)
    {
        if (range.HasValue && float.IsFinite(range.Value))
            VisionRange = Math.Clamp(range.Value, 0.25f, 1.6f);
        if (angle.HasValue && float.IsFinite(angle.Value))
            FieldOfViewDegrees = Math.Clamp(angle.Value, 40f, 360f);
        SampleSensors();
    }

    private static float RadiusFor(string kind) => kind switch
    {
        "rock" => 0.065f,
        "shelter" => 0.082f,
        "hazard" => 0.055f,
        _ => 0.043f
    };

    private bool IsFree(float x, float y, float radius)
    {
        if (MathF.Abs(x) + radius > 0.95f || MathF.Abs(y) + radius > 0.95f) return false;
        if (x * x + y * y < (radius + 0.13f) * (radius + 0.13f)) return false;
        foreach (WorldObject item in _objects)
        {
            float dx = item.X - x, dy = item.Y - y;
            if (dx * dx + dy * dy < (radius + item.Radius + 0.045f) * (radius + item.Radius + 0.045f))
                return false;
        }
        return true;
    }

    private void SpawnRandom(string kind)
    {
        float radius = RadiusFor(kind);
        for (int attempt = 0; attempt < 64; attempt++)
        {
            float x = (float)(_random.NextDouble() * 1.7 - 0.85);
            float y = (float)(_random.NextDouble() * 1.7 - 0.85);
            if (!IsFree(x, y, radius)) continue;
            _objects.Add(new WorldObject { X = x, Y = y, Kind = kind, Radius = radius });
            return;
        }
    }

    public void Spawn(float x, float y, bool hazard = false, string? kind = null)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return;
        string selected = kind switch
        {
            "food" => "food", "hazard" => "hazard", "rock" => "rock",
            "shelter" => "shelter", _ => hazard ? "hazard" : "food"
        };
        float radius = RadiusFor(selected);
        x = Math.Clamp(x, -0.95f + radius, 0.95f - radius);
        y = Math.Clamp(y, -0.95f + radius, 0.95f - radius);
        if (selected == "rock" && MathF.Pow(X - x, 2f) + MathF.Pow(Y - y, 2f) < MathF.Pow(radius + AgentRadius, 2f))
            return; // Do not trap the agent inside a newly spawned solid rock.
        if (_objects.Count >= 90) _objects.RemoveAt(0);
        _objects.Add(new WorldObject { X = x, Y = y, Kind = selected, Radius = radius });
        SampleSensors();
    }

    private static float WrapAngle(float x) => MathF.Atan2(MathF.Sin(x), MathF.Cos(x));

    private bool InSight(WorldObject o)
    {
        float dx = o.X - X, dy = o.Y - Y;
        float distance2 = dx * dx + dy * dy;
        float distance = MathF.Sqrt(distance2);
        if (distance > EffectiveVisionRange + o.Radius) return false;
        float angle = MathF.Abs(WrapAngle(MathF.Atan2(dy, dx) - Heading));
        if (angle > FieldOfViewDegrees * MathF.PI / 360f +
            MathF.Asin(Math.Clamp(o.Radius / MathF.Max(distance, o.Radius), 0f, 1f))) return false;
        // A rock between the agent and an object occludes the object. Segment projection
        // avoids allocating raycast results; no full visibility grid is needed.
        foreach (WorldObject blocking in _objects)
        {
            if (blocking.Kind != "rock" || ReferenceEquals(blocking, o)) continue;
            float ax = blocking.X - X, ay = blocking.Y - Y;
            if (distance2 < 0.000001f) break;
            float t = (ax * dx + ay * dy) / distance2;
            if (t <= 0.01f || t >= 0.99f) continue;
            float nearestX = X + t * dx - blocking.X;
            float nearestY = Y + t * dy - blocking.Y;
            if (nearestX * nearestX + nearestY * nearestY < blocking.Radius * blocking.Radius)
                return false;
        }
        return true;
    }

    public ReadOnlySpan<float> SampleSensors()
    {
        Array.Clear(_sensors);
        _visibleObjects = 0;
        float visionRange = MathF.Max(0.01f, EffectiveVisionRange);
        float halfFov = FieldOfViewDegrees * MathF.PI / 360f;
        foreach (WorldObject o in _objects)
        {
            if (!InSight(o)) continue;
            _visibleObjects++;
            float dx = o.X - X, dy = o.Y - Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            float angle = WrapAngle(MathF.Atan2(dy, dx) - Heading);
            float signal = 2.8f * MathF.Pow(Math.Clamp(1f - (distance - o.Radius) / visionRange, 0f, 1f), 1.35f);
            if (o.Kind == "rock")
            {
                // Rock receptor is most sensitive in the forward corridor.
                if (MathF.Abs(angle) < MathF.PI / 3.2f)
                    _sensors[9] = MathF.Max(_sensors[9], signal);
                continue;
            }
            int bearing = angle < -halfFov / 3f ? 0 : angle > halfFov / 3f ? 2 : 1;
            int offset = o.Kind switch { "food" => 0, "hazard" => 3, "shelter" => 6, _ => -1 };
            if (offset >= 0) _sensors[offset + bearing] = Math.Clamp(_sensors[offset + bearing] + signal, 0f, 3f);
        }
        // Proprioceptive receptors work independently of light and occlusion.
        _sensors[10] = Math.Clamp((0.78f - Energy) * 3.0f, 0f, 3f);
        _sensors[11] = Math.Clamp(Exposure * 3f, 0f, 3f);
        return _sensors;
    }

    private void UpdateClimate()
    {
        Daylight = 0.5f + 0.5f * MathF.Cos(TwoPi * (_steps % 5400) / 5400f - 0.8f);
        AmbientTemperature = Math.Clamp(0.25f * MathF.Sin(X * 4.5f) +
            0.20f * MathF.Sin(Y * 3f + _steps * 0.004f) - 0.65f * (1f - Daylight), -1f, 1f);
        float protection = 0f;
        foreach (WorldObject item in _objects)
        {
            if (item.Kind != "shelter") continue;
            float d = MathF.Sqrt((item.X - X) * (item.X - X) + (item.Y - Y) * (item.Y - Y));
            protection = MathF.Max(protection, Math.Clamp((0.24f - d) / 0.24f, 0f, 1f));
        }
        Exposure = Math.Clamp(MathF.Abs(AmbientTemperature) * (1f - protection * 0.85f), 0f, 1f);
    }

    public WorldOutcome Step(float[] motors, float dt = 1f / 30f)
    {
        if (motors.Length != 3) throw new ArgumentException("Expected three motor channels.", nameof(motors));
        _steps++;
        float beforeEnergy = Energy;
        int beforeFood = FoodCollected;
        bool danger = false;
        Array.Copy(motors, _motors, 3);
        float left = Math.Clamp(float.IsFinite(motors[0]) ? motors[0] : 0f, 0f, 1f);
        float right = Math.Clamp(float.IsFinite(motors[1]) ? motors[1] : 0f, 0f, 1f);
        float thrust = Math.Clamp(float.IsFinite(motors[2]) ? motors[2] : 0f, 0f, 1f);
        Heading += ((right - left) * 2.8f + 0.11f * MathF.Sin(_steps * 0.011f)) * dt;
        // No forced forward motion: the agent can stop when motor drive is absent.
        float speed = 0.45f * thrust;
        float nx = X + MathF.Cos(Heading) * speed * dt;
        float ny = Y + MathF.Sin(Heading) * speed * dt;
        bool blocked = nx < -0.95f || nx > 0.95f || ny < -0.95f || ny > 0.95f;
        nx = Math.Clamp(nx, -0.95f, 0.95f);
        ny = Math.Clamp(ny, -0.95f, 0.95f);
        foreach (WorldObject item in _objects)
        {
            if (item.Kind != "rock") continue;
            float dx = nx - item.X, dy = ny - item.Y;
            float limit = AgentRadius + item.Radius;
            float distance2 = dx * dx + dy * dy;
            if (distance2 >= limit * limit) continue;
            blocked = true;
            float distance = MathF.Sqrt(distance2);
            if (distance < 0.00001f) { dx = MathF.Cos(Heading + MathF.PI); dy = MathF.Sin(Heading + MathF.PI); distance = 1f; }
            nx = item.X + dx / distance * limit;
            ny = item.Y + dy / distance * limit;
        }
        float oldX = X, oldY = Y;
        X = Math.Clamp(nx, -0.95f, 0.95f);
        Y = Math.Clamp(ny, -0.95f, 0.95f);
        _distanceTravelled += MathF.Sqrt((X - oldX) * (X - oldX) + (Y - oldY) * (Y - oldY));
        if (_fixedCourse) _bestGoalDistance = MathF.Min(_bestGoalDistance, GoalDistance);
        if (blocked) { _collisions++; Heading += 0.19f; }
        UpdateClimate();
        Energy = Math.Clamp(Energy - dt * (0.004f + speed * 0.012f + Exposure * 0.010f), 0f, 1f);
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            WorldObject item = _objects[i];
            float dx = X - item.X, dy = Y - item.Y;
            if (dx * dx + dy * dy > (AgentRadius + item.Radius) * (AgentRadius + item.Radius)) continue;
            if (item.Kind == "hazard")
            {
                danger = true;
                Energy = Math.Clamp(Energy - dt * 0.22f, 0f, 1f);
            }
            if (item.Kind == "food")
            {
                Energy = Math.Clamp(Energy + 0.19f, 0f, 1f);
                FoodCollected++;
                _objects.RemoveAt(i);
                if (!_fixedCourse) SpawnRandom("food");
            }
            if (item.Kind == "shelter") Energy = Math.Clamp(Energy + dt * 0.012f, 0f, 1f);
        }
        if (danger) _hazardContacts++;
        SampleSensors();
        return new WorldOutcome(FoodCollected - beforeFood, danger, blocked, Energy - beforeEnergy);
    }

    public WorldView Snapshot() => new(X, Y, Heading, Energy, FoodCollected,
        (float[])_sensors.Clone(), (float[])_motors.Clone(),
        _objects.Select(o => new WorldObjectView(o.X, o.Y, o.Kind == "hazard", o.Kind, o.Radius, InSight(o))).ToArray(),
        VisionRange, EffectiveVisionRange, FieldOfViewDegrees, Daylight, AmbientTemperature,
        Exposure, _visibleObjects, _collisions, _course, GoalDistance, _bestGoalDistance, _distanceTravelled, _hazardContacts);
}
