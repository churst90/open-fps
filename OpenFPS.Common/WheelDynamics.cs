using System.Collections.Concurrent;

namespace OpenFPS.Common;

/// <summary>
/// One Magic Formula curve with its peak scaled to one: y = sin(C atan(t - E (t - atan t))), with
/// t = B x (Pacejka, Tire and Vehicle Dynamics, 2nd ed. 2006, chapter 4). Odd in t. Shared by
/// every tyre with the same C and E.
/// </summary>
public sealed class MagicCurve
{
    public float C { get; }
    public float E { get; }
    /// <summary>B x at the peak of the curve.</summary>
    public float PeakT { get; }

    private const int Samples = 128;
    /// <summary>t on the rising branch for y = i / Samples.</summary>
    private readonly float[] _inverse = new float[Samples + 1];

    private static readonly ConcurrentDictionary<(float, float), MagicCurve> Cache = new();

    public static MagicCurve Of(float c, float e) => Cache.GetOrAdd((c, e), k => new MagicCurve(k.Item1, k.Item2));

    private MagicCurve(float c, float e)
    {
        C = c; E = e;
        // The peak: scanned rather than solved, because E moves it and for C <= 1 there is none
        // (the curve rises for ever toward sin(C pi/2)); 60 is far past any tyre's working range.
        float best = 0f, bestT = 60f;
        for (float t = 0.01f; t <= 60f; t += 0.01f)
        {
            float y = Raw(t);
            if (y > best) { best = y; bestT = t; }
            else if (y < best - 1e-4f) break;
        }
        PeakT = bestT;
        float peak = Raw(PeakT);
        for (int i = 0; i <= Samples; i++)
        {
            float want = peak * i / Samples;
            float lo = 0f, hi = PeakT;
            for (int k = 0; k < 40; k++)
            {
                float mid = 0.5f * (lo + hi);
                if (Raw(mid) < want) lo = mid; else hi = mid;
            }
            _inverse[i] = 0.5f * (lo + hi);
        }
        _peak = peak;
    }

    private readonly float _peak;

    private float Raw(float t) => MathF.Sin(C * MathF.Atan(t - E * (t - MathF.Atan(t))));

    /// <summary>The curve at t, scaled so the peak is one.</summary>
    public float At(float t)
    {
        float a = MathF.Abs(t);
        float y = Raw(a) / _peak;
        return t < 0f ? -y : y;
    }

    /// <summary>The t on the rising branch where the curve reaches |y| (0..1), signed like y.</summary>
    public float Inverse(float y)
    {
        float a = Math.Clamp(MathF.Abs(y), 0f, 1f) * Samples;
        int i = Math.Min(Samples - 1, (int)a);
        float f = a - i;
        float t = _inverse[i] + (_inverse[i + 1] - _inverse[i]) * f;
        return y < 0f ? -t : t;
    }
}

/// <summary>
/// A vehicle on its wheels, in the plane of the road: each wheel's load, slip and speed, the tyre
/// forces they make, and the body's motion under them.
///
/// The body is the planar three-degree-of-freedom model (longitudinal, lateral, yaw) with a wheel
/// at every position the preset declares (Gillespie, Fundamentals of Vehicle Dynamics, 1992,
/// chapter 6; Milliken and Milliken, Race Car Vehicle Dynamics, 1995, chapter 5):
///
///   m (dVx/dt - r Vy) = sum Fx,   m (dVy/dt + r Vx) = sum Fy,   Iz dr/dt = sum (x Fy - y Fx)
///
/// in the body's frame: x forward, y to the right, r positive turning right (the game's heading
/// rises turning right).
///
/// Each wheel's load is its static share plus the load transfer from the last step's
/// accelerations: m ax h / wheelbase between the front and rear axle groups, and m ay h / track
/// across each axle, divided between the axles by the declared roll-stiffness share or, when none
/// is declared, by their static loads. Tandem axles share their group's load equally, as a
/// load-equalising suspension makes them.
///
/// Each tyre's lateral force comes from its slip angle through the Magic Formula
/// (<see cref="TyreProfile"/>), its peak from the grip, the surface and the load. The longitudinal
/// force is what the driver asks of it — drive split equally over the driven wheels (an open
/// differential), braking in proportion to load (the ideal brake proportioning of Limpert, Brake
/// Design and Safety, 2nd ed. 1999) — held to the friction circle left over by the cornering
/// force (the friction circle: Milliken and Milliken 1995, chapter 2). Asked for more than the
/// circle, the wheel slides, and both forces are scaled back onto it. The slip ratio is the one at
/// which the longitudinal Magic Formula gives that force, and the wheel's speed follows from it:
/// omega = u (1 + kappa) / rolling radius.
///
/// Below walking pace slip angles mean nothing (they divide by a speed going to zero), so under
/// <see cref="KinematicBelow"/> the yaw rate and sideslip are the kinematic bicycle's, blending into
/// the dynamic model by <see cref="DynamicAbove"/>: the arrangement Kong, Pfeiffer, Schildbach and
/// Borrelli (IEEE Intelligent Vehicles Symposium 2015) show to be accurate at low speed.
/// </summary>
public sealed class WheelDynamics
{
    public const float G = TyreFriction.G;

    /// <summary>Speeds the kinematic model hands over to the dynamic one between, m/s.</summary>
    public const float KinematicBelow = 3f, DynamicAbove = 6f;

    public struct Wheel
    {
        /// <summary>Metres forward of, and to the right of, the centre of gravity.</summary>
        public float X, Y;
        public int Axle;
        /// <summary>On an axle of the front group (see <see cref="ChassisSpec.Groups"/>).</summary>
        public bool Front;
        public bool Steered, Driven;
        public float Radius;
        /// <summary>Its load standing still on the flat, newtons: the tyre's nominal load.</summary>
        public float StaticLoad;

        // ── This step ──
        public float Load;
        public float SteerAngle;
        /// <summary>Slip ratio: -1 locked, 0 free rolling, positive spinning up.</summary>
        public float SlipRatio;
        /// <summary>Slip angle, radians, positive when the contact patch moves to the right of where it points.</summary>
        public float SlipAngle;
        /// <summary>Radians a second, forward positive.</summary>
        public float AngularSpeed;
        /// <summary>Forces on the tyre, in its own frame: along it and to its right, newtons.</summary>
        public float Fx, Fy;
        /// <summary>Its share of its grip in use: 1 at the peak, over 1 sliding. See TyreFriction.</summary>
        public float Demand;
        /// <summary>The surface under it (<see cref="RoadSurfaces"/>) and the grip that gives.</summary>
        public byte Surface;
        public float SurfaceGrip;
        /// <summary>The water under it, mm from the bottom of the road's texture (RoadWater), and the
        /// share of its dry grip that leaves it at the body's speed (RoadWaterLaw.GripFactor), worked
        /// out at the start of each step.</summary>
        public float Water, WetGrip;

        // ── Working values of the step ──
        /// <summary>Its peak force at its load, the lateral Magic Formula's B, and the lateral force its
        /// slip angle alone makes, newtons.</summary>
        public float Peak, LateralB, LateralForce;
    }

    public Wheel[] Wheels { get; }
    public ChassisSpec Chassis { get; }
    public TyreProfile Tyre { get; }

    /// <summary>Peak friction on dry asphalt, g. The preset's tyre unless the map says otherwise.</summary>
    public float GripG { get; set; }

    /// <summary>Never rolls backwards: traffic, whose speed logic has no reverse.</summary>
    public bool ForwardOnly { get; set; }

    /// <summary>Driven by somebody who feels the car (traffic): throttle and brake are eased so no wheel
    /// is asked for more than its friction circle leaves. Never a player's car.</summary>
    public bool Modulated { get; set; }

    /// <summary>
    /// For a modulated driver, the share of a driven wheel's grip the throttle may use beside the
    /// cornering: <see cref="TyreFriction.SquealOnset"/>, the point where a tyre starts to sing,
    /// because pulling away does not make an ordinary driver's tyres squeal.
    /// </summary>
    public float PullingUse { get; set; } = TyreFriction.SquealOnset;

    /// <summary>The same for the brakes: all of the circle. Held to the squeal onset, traffic could
    /// not shed speed beside the cornering at a junction's entry and arrived too fast.</summary>
    public float BrakingUse { get; set; } = 1f;

    public float Vx, Vy, YawRate;
    /// <summary>The body's acceleration last step, in its own frame, m/s^2: what the loads moved for.</summary>
    public float Ax, Ay;
    /// <summary>The road-wheel angle the driver has on, radians, right positive.</summary>
    public float SteerAngle { get; private set; }

    /// <summary>Over the last <see cref="Step"/>: the yaw turned, and the ground covered in the
    /// frame the body had at its start, forward and to the right.</summary>
    public float TickYaw, TickForward, TickRight;

    public float Mass { get; }
    public float YawInertia { get; }
    /// <summary>Centre of gravity to the front and rear axle groups, metres.</summary>
    public float A { get; }
    public float B { get; }
    public float Wheelbase => A + B;
    /// <summary>Understeer gradient at the static loads, radians of steer per m/s^2 of lateral
    /// acceleration (Gillespie 1992, chapter 6): m/L (b/Cf - a/Cr).</summary>
    public float UndersteerGradient { get; }

    private readonly MagicCurve _lat, _lon;
    private readonly float _rearStiffness;

    /// <summary>
    /// The body's sideslip in a steady turn on the linear part of the tyres, radians, positive when it
    /// is moving to the right of where it points: k (b - m a v^2 / (L C_r)) with C_r the rear axle
    /// group's cornering stiffness (Gillespie 1992, chapter 6). At walking pace the body follows the
    /// rear axle round and moves a little into the turn; at speed its nose points into the turn.
    /// </summary>
    public float SteadySideslip(float speed, float curvature)
        => _rearStiffness > 0f ? curvature * (B - Mass * A * speed * speed / (MathF.Max(0.1f, A + B) * _rearStiffness)) : 0f;
    private readonly float[] _axleStatic, _rollShare;
    private readonly int[] _axleWheels;
    private readonly bool[] _axleFront;
    private readonly int _frontAxles, _rearAxles;
    /// <summary>Sum of cornering stiffness over mass and of x^2 times it over yaw inertia: the
    /// fastest rates the lateral motion has, at 1 m/s. They set how finely a step is cut.</summary>
    private readonly float _lateralRate;
    private float _induced;

    public WheelDynamics(VehicleProfile profile, float? gripG = null)
    {
        Chassis = profile.Running;
        Tyre = profile.Tyres;
        GripG = gripG ?? profile.Tyres.PeakGripG;
        Mass = MathF.Max(1f, profile.MassKg);
        _lat = MagicCurve.Of(Tyre.LateralShape, Tyre.LateralCurvature);
        _lon = MagicCurve.Of(Tyre.LongitudinalShape, Tyre.LongitudinalCurvature);

        var axles = Chassis.Axles;
        int n = axles.Length;
        _axleStatic = new float[n];
        _rollShare = new float[n];
        _axleWheels = new int[n];
        _axleFront = new bool[n];
        float cog = Chassis.CentreOfGravityZ;
        bool grouped = Chassis.Groups(out float frontMiddle, out float rearMiddle);
        float mid = grouped ? 0.5f * (axles.Max(a => a.Z) + axles.Min(a => a.Z)) : float.NegativeInfinity;
        for (int k = 0; k < n; k++)
        {
            _axleFront[k] = grouped && axles[k].Z > mid;
            if (_axleFront[k]) _frontAxles++; else _rearAxles++;
            _axleWheels[k] = axles[k].Wheels;
        }
        float weight = Mass * G;
        if (_frontAxles > 0 && _rearAxles > 0 && frontMiddle > cog && cog > rearMiddle)
        {
            A = frontMiddle - cog;
            B = cog - rearMiddle;
            float wf = weight * B / (A + B), wr = weight * A / (A + B);
            for (int k = 0; k < n; k++) _axleStatic[k] = _axleFront[k] ? wf / _frontAxles : wr / _rearAxles;
        }
        else
        {
            // One axle, or every axle on one side of the centre of gravity: it cannot stand up on
            // them; shared equally.
            A = B = 1f;
            _frontAxles = n; _rearAxles = 0;
            for (int k = 0; k < n; k++) _axleFront[k] = true;
            for (int k = 0; k < n; k++) _axleStatic[k] = weight / Math.Max(1, n);
        }

        // Lateral load transfer: only axles with two wheels can take any.
        float frontStatic = 0f, rearStatic = 0f;
        for (int k = 0; k < n; k++)
            if (axles[k].TrackMetres > 0f) { if (_axleFront[k]) frontStatic += _axleStatic[k]; else rearStatic += _axleStatic[k]; }
        float twoWheeled = frontStatic + rearStatic;
        for (int k = 0; k < n; k++)
        {
            if (axles[k].TrackMetres <= 0f || twoWheeled <= 0f) continue;
            if (Chassis.FrontRollStiffnessShare is float s && frontStatic > 0f && rearStatic > 0f)
                _rollShare[k] = _axleFront[k] ? s * _axleStatic[k] / frontStatic : (1f - s) * _axleStatic[k] / rearStatic;
            else
                _rollShare[k] = _axleStatic[k] / twoWheeled;
        }

        var wheels = new List<Wheel>();
        for (int k = 0; k < n; k++)
        {
            var ax = axles[k];
            float x = ax.Z - cog;
            float each = _axleStatic[k] / _axleWheels[k];
            void Add(float y) => wheels.Add(new Wheel
            {
                X = x, Y = y, Axle = k, Front = _axleFront[k], Steered = ax.Steered, Driven = ax.Driven,
                Radius = MathF.Max(0.05f, ax.Tyre.RollingRadiusMetres), StaticLoad = each, Load = each,
                Surface = RoadSurfaces.IndexOf(RoadData.DefaultSurface), SurfaceGrip = 1f, WetGrip = 1f,
            });
            if (ax.TrackMetres > 0f) { Add(-0.5f * ax.TrackMetres); Add(0.5f * ax.TrackMetres); }
            else Add(0f);
        }
        Wheels = wheels.ToArray();

        YawInertia = MathF.Max(1f, Chassis.YawInertiaIndex * Mass * A * B);

        float sumC = 0f, sumX2C = 0f, cf = 0f, cr = 0f;
        foreach (var w in Wheels)
        {
            float c = CorneringStiffness(w.StaticLoad, w.StaticLoad);
            sumC += c; sumX2C += w.X * w.X * c;
            if (w.Front) cf += c; else cr += c;
        }
        _lateralRate = MathF.Max(sumC / Mass, sumX2C / YawInertia);
        UndersteerGradient = cf > 0f && cr > 0f ? Mass / (A + B) * (B / cf - A / cr) : 0f;
        _rearStiffness = cr;
    }

    /// <summary>K = PKY1 Fz0 sin(2 atan(Fz / (PKY2 Fz0))), newtons per radian.</summary>
    private float CorneringStiffness(float load, float nominal)
        => Tyre.CorneringStiffness * nominal * MathF.Sin(2f * MathF.Atan(load / MathF.Max(1f, Tyre.CorneringStiffnessLoad * nominal)));

    /// <summary>The friction coefficient of a wheel at its load, on its surface, with the water on it.</summary>
    private float Mu(in Wheel w)
    {
        float dfz = (w.Load - w.StaticLoad) / MathF.Max(1f, w.StaticLoad);
        float wet = _dryTable ? 1f : w.WetGrip;
        return MathF.Max(0.05f, GripG) * w.SurfaceGrip * wet * MathF.Max(0.2f, 1f + Tyre.LoadSensitivity * dfz);
    }

    /// <summary>While the steady-turn table is built: the dry road, so the table is the vehicle's own and
    /// a wet road is read off it by <see cref="WetGripShare"/>.</summary>
    private bool _dryTable;

    /// <summary>Every wheel's share of its dry grip with the water under it, once a step: the aquaplaning
    /// law is too dear for every sub-step of every wheel, and the speed changes little over one.</summary>
    private void Wet()
    {
        for (int i = 0; i < Wheels.Length; i++)
        {
            ref var w = ref Wheels[i];
            w.WetGrip = w.Water > 0f ? RoadWaterLaw.GripFactor(w.Surface, w.Water, Vx, Tyre.InflationKPa, Tyre.TreadDepthMm) : 1f;
        }
    }

    /// <summary>Sets the water under one wheel, mm (RoadWater).</summary>
    public void SetWater(int wheel, float waterMm) => Wheels[wheel].Water = float.IsFinite(waterMm) ? MathF.Max(0f, waterMm) : 0f;

    /// <summary>The least share of its dry grip any wheel has on the water under it now: what a driver
    /// who feels the car takes the bends and the brakes by.</summary>
    public float WetGripShare
    {
        get
        {
            float least = 1f;
            foreach (var w in Wheels) least = MathF.Min(least, w.WetGrip);
            return least;
        }
    }

    /// <summary>Stopped where it stands: no speed, no yaw, nothing slipping.</summary>
    public void Halt()
    {
        Vx = Vy = YawRate = Ax = Ay = 0f;
        _induced = 0f;
        TickYaw = TickForward = TickRight = 0f;
        for (int i = 0; i < Wheels.Length; i++)
        {
            ref var w = ref Wheels[i];
            w.Load = w.StaticLoad; w.SlipAngle = w.SlipRatio = w.AngularSpeed = w.Fx = w.Fy = w.Demand = 0f;
        }
    }

    /// <summary>Sets the surface under one wheel.</summary>
    public void SetSurface(int wheel, byte surface)
    {
        Wheels[wheel].Surface = surface;
        Wheels[wheel].SurfaceGrip = RoadSurfaces.GripOf(surface);
    }

    /// <summary>Sets the surface under every wheel.</summary>
    public void SetSurface(byte surface)
    {
        float grip = RoadSurfaces.GripOf(surface);
        for (int i = 0; i < Wheels.Length; i++) { Wheels[i].Surface = surface; Wheels[i].SurfaceGrip = grip; }
    }

    /// <summary>The largest demand on any wheel this step.</summary>
    public float MaxDemand
    {
        get
        {
            float d = 0f;
            foreach (var w in Wheels) d = MathF.Max(d, w.Demand);
            return d;
        }
    }

    // ── Loads ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every wheel's load for these accelerations in the body's frame (forward, rightward, and the
    /// acceleration pressing it into the road: g on the flat).
    /// </summary>
    public void Loads(float ax, float ay, float normal = G)
    {
        var axles = Chassis.Axles;
        float h = Chassis.CentreOfGravityHeightMetres;
        float shift = _frontAxles > 0 && _rearAxles > 0 ? Mass * ax * h / (A + B) : 0f;
        float scale = normal / G;
        int i = 0;
        for (int k = 0; k < axles.Length; k++)
        {
            float axle = _axleStatic[k] * scale + (_axleFront[k] ? -shift / _frontAxles : shift / _rearAxles);
            axle = MathF.Max(0f, axle);
            if (_axleWheels[k] == 2)
            {
                // Turning right (ay > 0) the left wheel is on the outside and gains.
                float across = _rollShare[k] * Mass * ay * h / axles[k].TrackMetres;
                float left = 0.5f * axle + across, right = 0.5f * axle - across;
                if (left < 0f) { right = axle; left = 0f; }
                if (right < 0f) { left = axle; right = 0f; }
                Wheels[i++].Load = left;
                Wheels[i++].Load = right;
            }
            else Wheels[i++].Load = axle;
        }
    }

    // ── Steering ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Each steered wheel's own angle for a bicycle-model angle: Ackermann geometry about
    /// the rear axle group's turning centre, blended with parallel steer by the chassis' share.</summary>
    private void SteerWheels(float steer)
    {
        float k = Chassis.Ackermann;
        float t = MathF.Tan(steer);
        for (int i = 0; i < Wheels.Length; i++)
        {
            ref var w = ref Wheels[i];
            if (!w.Steered) { w.SteerAngle = 0f; continue; }
            if (MathF.Abs(t) < 1e-5f || w.Y == 0f || k <= 0f) { w.SteerAngle = steer; continue; }
            float reach = w.X + B;                       // this axle to the rear group
            float centre = reach / t;                    // signed: right of the body for a right turn
            float ackermann = MathF.Atan(reach / (centre - w.Y));
            if (MathF.Sign(ackermann) != MathF.Sign(steer)) ackermann = steer;
            w.SteerAngle = steer + (ackermann - steer) * k;
        }
    }

    // ── Free motion ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Moves the body one step under its tyres with the wheel at <paramref name="steer"/> (radians
    /// at the road, right positive) and the driver asking for <paramref name="wantAccel"/> along the
    /// body. The step is cut finely enough that the fastest lateral motion is resolved.
    /// </summary>
    public void Step(float dt, float steer, float wantAccel)
    {
        SteerAngle = steer;
        SteerWheels(steer);
        TickYaw = TickForward = TickRight = 0f;
        if (dt <= 0f) return;
        Wet();

        float speed = MathF.Max(MathF.Abs(Vx), KinematicBelow);
        int sub = Math.Clamp((int)MathF.Ceiling(dt * _lateralRate / speed / 0.4f), 1, 16);
        float h = dt / sub;
        float yaw = 0f;
        for (int s = 0; s < sub; s++)
        {
            Loads(Ax, Ay);
            Forces(Mass * (wantAccel - YawRate * Vy) + _induced, out float fx, out float fy, out float mz, out _induced);
            float vx0 = Vx;
            Vx += (fx / Mass + YawRate * Vy) * h;
            Vy += (fy / Mass - YawRate * vx0) * h;
            YawRate += mz / YawInertia * h;
            Ax = fx / Mass;
            Ay = fy / Mass;
            if (ForwardOnly && Vx < 0f) Vx = 0f;

            // Walking pace and reversing: the kinematic bicycle about the rear axle group, blended
            // into the dynamic model as the speed rises.
            float dyn = Vx <= 0f ? 0f : Smooth(KinematicBelow, DynamicAbove, Vx);
            if (dyn < 1f)
            {
                float rKin = Vx * MathF.Tan(SteerAngle) / MathF.Max(0.5f, A + B);
                float vyKin = rKin * B;
                YawRate = rKin + (YawRate - rKin) * dyn;
                Vy = vyKin + (Vy - vyKin) * dyn;
            }

            float c = MathF.Cos(yaw), sn = MathF.Sin(yaw);
            TickForward += (Vx * c - Vy * sn) * h;
            TickRight += (Vx * sn + Vy * c) * h;
            yaw += YawRate * h;
        }
        TickYaw = yaw;
        Spin();
    }

    /// <summary>
    /// The tyre forces for the current motion, with <paramref name="drive"/> newtons asked of the
    /// road along the body; summed onto the body. <paramref name="induced"/> is the part of the
    /// cornering forces pulling backward, which the driver must also overcome to hold a speed.
    /// </summary>
    private void Forces(float drive, out float fx, out float fy, out float mz, out float induced)
    {
        fx = fy = mz = induced = 0f;
        float driven = 0f, total = 0f;
        for (int i = 0; i < Wheels.Length; i++)
        {
            ref var w = ref Wheels[i];
            Lateral(ref w);
            total += w.Load;
            if (w.Driven) driven++;
        }

        // A driver feeling the car asks no more of the throttle or the brake than the tyres have left
        // after the corner: the most the drive can be before one driven wheel reaches the share of
        // its friction circle a driver pulls with (or, braking, one wheel reaches the circle).
        if (Modulated && drive != 0f && total > 0f)
        {
            float most = float.PositiveInfinity;
            foreach (var w in Wheels)
            {
                if (w.Load <= 1f) continue;
                float share = drive > 0f ? (driven > 0f ? (w.Driven ? 1f / driven : 0f) : w.Load / total) : w.Load / total;
                if (share <= 0f) continue;
                float circle = (drive > 0f ? PullingUse : BrakingUse) * w.Peak;
                float room = MathF.Sqrt(MathF.Max(0f, circle * circle - w.LateralForce * w.LateralForce));
                most = MathF.Min(most, room / share);
            }
            if (float.IsFinite(most)) drive = Math.Clamp(drive, -most, most);
        }

        for (int i = 0; i < Wheels.Length; i++)
        {
            ref var w = ref Wheels[i];
            float want;
            if (drive >= 0f) want = driven > 0f ? (w.Driven ? drive / driven : 0f) : (total > 0f ? drive * w.Load / total : 0f);
            else want = total > 0f ? drive * w.Load / total : 0f;
            Longitudinal(ref w, want);
            float cs = MathF.Cos(w.SteerAngle), sn = MathF.Sin(w.SteerAngle);
            float bx = w.Fx * cs - w.Fy * sn, by = w.Fx * sn + w.Fy * cs;
            fx += bx; fy += by;
            mz += w.X * by - w.Y * bx;
            induced += w.Fy * sn;
        }
    }

    /// <summary>One tyre's slip angle from its motion, its peak force, and the lateral force the slip
    /// angle makes on its own.</summary>
    private void Lateral(ref Wheel w)
    {
        float u = Vx - YawRate * w.Y, v = Vy + YawRate * w.X;
        float cs = MathF.Cos(w.SteerAngle), sn = MathF.Sin(w.SteerAngle);
        float along = u * cs + v * sn, across = -u * sn + v * cs;
        w.SlipAngle = MathF.Atan2(across, MathF.Max(MathF.Abs(along), 0.5f));
        if (w.Load <= 1f) { w.Peak = w.LateralForce = w.LateralB = 0f; return; }
        w.Peak = Mu(w) * w.Load;
        w.LateralB = CorneringStiffness(w.Load, w.StaticLoad) / MathF.Max(1e-3f, Tyre.LateralShape * w.Peak);
        w.LateralForce = -w.Peak * _lat.At(w.LateralB * w.SlipAngle);
    }

    /// <summary>As much of the longitudinal force asked of a tyre as the friction circle leaves beside
    /// its lateral force; asked for more, it slides, and both are scaled back onto the circle.</summary>
    private void Longitudinal(ref Wheel w, float wantFx)
    {
        if (w.Load <= 1f) { w.Fx = w.Fy = 0f; w.Demand = 0f; w.SlipRatio = 0f; return; }
        float peak = w.Peak, fy0 = w.LateralForce;
        float room = MathF.Sqrt(MathF.Max(0f, peak * peak - fy0 * fy0));
        float slipUse = MathF.Abs(w.LateralB * w.SlipAngle) / _lat.PeakT;
        if (MathF.Abs(wantFx) <= room * 1.0001f)
        {
            w.Fx = wantFx; w.Fy = fy0;
            w.Demand = MathF.Max(MathF.Sqrt(wantFx * wantFx + fy0 * fy0) / peak, slipUse > 1f ? slipUse : 0f);
            w.SlipRatio = SlipRatio(w, wantFx, peak);
        }
        else
        {
            float over = MathF.Sqrt(wantFx * wantFx + fy0 * fy0) / peak;
            w.Fx = wantFx / over; w.Fy = fy0 / over;
            w.Demand = MathF.Max(over, slipUse);
            float kx = w.Load * (Tyre.SlipStiffness + Tyre.SlipStiffnessLoad * (w.Load - w.StaticLoad) / MathF.Max(1f, w.StaticLoad));
            float bx = kx / MathF.Max(1e-3f, Tyre.LongitudinalShape * peak);
            w.SlipRatio = Math.Clamp(MathF.Sign(wantFx) * _lon.PeakT / MathF.Max(1e-3f, bx) * over, -1f, 1f);
        }
    }

    /// <summary>The slip angle at which the steered wheels make their most force at their present loads,
    /// radians: as far past the front axle's motion as a driver can usefully turn the wheel.</summary>
    public float SteeredPeakSlip()
        => SteeredPeakSlipAt();

    /// <summary>
    /// The hardest steady turn on the flat, as a lateral acceleration in m/s^2, in which no tyre is
    /// worked past <paramref name="demand"/> of its grip (1 is the limit; <see cref="TyreFriction.SquealOnset"/>
    /// is where a tyre starts to squeal).
    ///
    /// In a steady turn each axle group makes its share of m ay by moments about the centre of gravity
    /// (front ay b / L, rear ay a / L); every wheel of a group runs at one slip angle, which is found
    /// by halving; each wheel's demand follows from its own load at that angle. So the light inside
    /// wheel, whose grip peaks at a smaller slip angle, is the one that sets the limit, as it should.
    /// </summary>
    public float SteadyLateralLimit(float demand = 1f)
    {
        float lo = 0f, hi = 3f * G;
        for (int k = 0; k < 32; k++)
        {
            float mid = 0.5f * (lo + hi);
            if (SteadyWorst(mid) <= demand) lo = mid; else hi = mid;
        }
        Loads(0f, 0f);
        return lo;
    }

    // ── Steady turns of a given radius ─────────────────────────────────────────────────────────

    private const int TurnTableSize = 24;
    private const float TightestRadius = 4f, WidestRadius = 400f;
    private float[]? _turnTable;
    private float _turnTableDemand = float.NaN;

    /// <summary>
    /// The fastest this vehicle can go round a steady turn of curvature <paramref name="curvature"/>
    /// (1/m, either sign) with no tyre worked past <paramref name="demand"/> of its grip, m/s.
    ///
    /// Worked out on the whole model, not the bicycle: at each speed the steady state is found — the
    /// sideslip and steering angle at which the tyres hold the turn with no yaw acceleration, the
    /// drive making up what the cornering drags back — and every wheel's demand read off it. So the
    /// geometry of a tight turn counts: the inside front of a car turning a junction runs at a bigger
    /// slip angle on less load than the outside, and it is the one that starts to sing first. Tabled
    /// once per vehicle over radii from 4 to 400 m and read by interpolation.
    /// </summary>
    public float SteadyTurnSpeed(float curvature, float demand)
    {
        float k = MathF.Abs(curvature);
        if (k < 1f / WidestRadius) return float.PositiveInfinity;
        if (_turnTable == null || _turnTableDemand != demand) BuildTurnTable(demand);
        float x = MathF.Log(MathF.Min(1f / TightestRadius, k) * TightestRadius) / MathF.Log(TightestRadius / WidestRadius) * (TurnTableSize - 1);
        x = Math.Clamp(x, 0f, TurnTableSize - 1);
        int i = Math.Min(TurnTableSize - 2, (int)x);
        float f = x - i;
        return _turnTable![i] + (_turnTable[i + 1] - _turnTable[i]) * f;
    }

    private void BuildTurnTable(float demand)
    {
        var table = new float[TurnTableSize];
        bool modulated = Modulated;
        float vx = Vx, vy = Vy, r = YawRate, ax = Ax, ay = Ay, steer = SteerAngle;
        Modulated = false;
        _dryTable = true;
        for (int i = 0; i < TurnTableSize; i++)
        {
            // Radius from the tightest to the widest, evenly in its logarithm.
            float radius = TightestRadius * MathF.Pow(WidestRadius / TightestRadius, i / (float)(TurnTableSize - 1));
            float lo = 0.5f, hi = 80f;
            for (int k = 0; k < 24; k++)
            {
                float mid = 0.5f * (lo + hi);
                if (SteadyTurn(mid, 1f / radius) <= demand) lo = mid; else hi = mid;
            }
            table[i] = lo;
        }
        Modulated = modulated;
        _dryTable = false;
        Vx = vx; Vy = vy; YawRate = r; Ax = ax; Ay = ay;
        SteerWheels(steer);
        _turnTable = table;
        _turnTableDemand = demand;
    }

    /// <summary>
    /// Puts the body into the steady turn at this speed and curvature (positive turning right) and
    /// returns the most-worked wheel's demand, or 2 when no steady state is found within the lock.
    /// The wheels are left as the turn has them.
    /// </summary>
    public float SteadyTurn(float speed, float curvature)
    {
        bool modulated = Modulated;
        Modulated = false;
        try { return SteadyTurnCore(speed, curvature); }
        finally { Modulated = modulated; }
    }

    private float SteadyTurnCore(float speed, float curvature)
    {
        float r = speed * curvature;
        float vy = 0f, steer = MathF.Atan(Wheelbase * curvature);
        float maxSteer = Chassis.MaxSteerAngleRad;
        for (int it = 0; it < 30; it++)
        {
            var (fy, mz) = SteadyResidual(speed, r, vy, steer);
            if (MathF.Abs(fy) < 1e-3f * Mass * G && MathF.Abs(mz) < 1e-3f * Mass * G * Wheelbase) break;
            // Newton on (sideslip, steer) with differences.
            const float dv = 1e-3f, ds = 1e-4f;
            var (fyV, mzV) = SteadyResidual(speed, r, vy + dv, steer);
            var (fyS, mzS) = SteadyResidual(speed, r, vy, steer + ds);
            float a11 = (fyV - fy) / dv, a12 = (fyS - fy) / ds, a21 = (mzV - mz) / dv, a22 = (mzS - mz) / ds;
            float det = a11 * a22 - a12 * a21;
            if (MathF.Abs(det) < 1e-9f) return 2f;
            float dVy = (-fy * a22 + mz * a12) / det, dSteer = (-a11 * mz + a21 * fy) / det;
            vy += Math.Clamp(dVy, -0.5f, 0.5f);
            steer += Math.Clamp(dSteer, -0.05f, 0.05f);
            if (MathF.Abs(steer) > maxSteer + 0.2f) return 2f;
        }
        var (rf, rm) = SteadyResidual(speed, r, vy, steer);
        if (MathF.Abs(rf) > 0.02f * Mass * G || MathF.Abs(rm) > 0.02f * Mass * G * Wheelbase || MathF.Abs(steer) > maxSteer) return 2f;
        return MaxDemand;
    }

    /// <summary>The lateral force and yaw moment left over in a turn held at this sideslip and steer:
    /// zero in the steady state. The drive is what holds the speed against what the turn drags.</summary>
    private (float Fy, float Mz) SteadyResidual(float speed, float r, float vy, float steer)
    {
        Vx = speed; Vy = vy; YawRate = r;
        SteerWheels(steer);
        Loads(0f, speed * r);
        float drive = 0f, fx = 0f, fy = 0f, mz = 0f;
        for (int pass = 0; pass < 2; pass++)
        {
            Forces(drive, out fx, out fy, out mz, out float induced);
            drive = -Mass * r * vy + induced;
        }
        return (fy - Mass * speed * r, mz);
    }

    /// <summary>The most-worked wheel's demand in a steady turn at this lateral acceleration (2 when
    /// an axle cannot make its share at all).</summary>
    private float SteadyWorst(float ay)
    {
        Loads(0f, ay);
        float worst = 0f;
        float l = A + B;
        for (int g = 0; g < 2; g++)
        {
            bool front = g == 0;
            float need = Mass * ay * (_frontAxles > 0 && _rearAxles > 0 ? (front ? B : A) / l : 0.5f);
            float alo = 0f, ahi = 0.6f;
            if (GroupForce(front, ahi) < need) return 2f;
            for (int k = 0; k < 30; k++)
            {
                float mid = 0.5f * (alo + ahi);
                if (GroupForce(front, mid) < need) alo = mid; else ahi = mid;
            }
            float alpha = ahi;
            foreach (var w in Wheels)
            {
                if (w.Front != front || w.Load <= 1f) continue;
                float peak = Mu(w) * w.Load;
                float b = CorneringStiffness(w.Load, w.StaticLoad) / MathF.Max(1e-3f, Tyre.LateralShape * peak);
                float use = MathF.Max(_lat.At(b * alpha), b * alpha / _lat.PeakT > 1f ? b * alpha / _lat.PeakT : 0f);
                worst = MathF.Max(worst, use);
            }
        }
        return worst;
    }

    /// <summary>A group's lateral force at one slip angle, rising branch only (past every wheel's
    /// peak it is held at the peak, so the halving finds the first angle that makes the force).</summary>
    private float GroupForce(bool front, float alpha)
    {
        float sum = 0f;
        foreach (var w in Wheels)
        {
            if (w.Front != front || w.Load <= 1f) continue;
            float peak = Mu(w) * w.Load;
            float b = CorneringStiffness(w.Load, w.StaticLoad) / MathF.Max(1e-3f, Tyre.LateralShape * peak);
            sum += peak * _lat.At(MathF.Min(b * alpha, _lat.PeakT));
        }
        return sum;
    }

    private float SteeredPeakSlipAt()
    {
        float sum = 0f; int n = 0;
        foreach (var w in Wheels)
        {
            if (!w.Steered || w.Load <= 1f) continue;
            float peak = Mu(w) * w.Load;
            float b = CorneringStiffness(w.Load, w.StaticLoad) / MathF.Max(1e-3f, Tyre.LateralShape * peak);
            sum += _lat.PeakT / MathF.Max(1e-3f, b);
            n++;
        }
        return n > 0 ? sum / n : 0.2f;
    }

    /// <summary>The slip ratio at which the longitudinal curve gives this force.</summary>
    private float SlipRatio(in Wheel w, float fx, float peak)
    {
        if (fx == 0f) return 0f;
        float kx = w.Load * (Tyre.SlipStiffness + Tyre.SlipStiffnessLoad * (w.Load - w.StaticLoad) / MathF.Max(1f, w.StaticLoad));
        float bx = kx / MathF.Max(1e-3f, Tyre.LongitudinalShape * peak);
        return Math.Clamp(_lon.Inverse(fx / peak) / MathF.Max(1e-3f, bx), -1f, 1f);
    }

    /// <summary>Each wheel's speed from its own ground speed along it and its slip.</summary>
    private void Spin()
    {
        for (int i = 0; i < Wheels.Length; i++)
        {
            ref var w = ref Wheels[i];
            float u = Vx - YawRate * w.Y, v = Vy + YawRate * w.X;
            float along = u * MathF.Cos(w.SteerAngle) + v * MathF.Sin(w.SteerAngle);
            w.AngularSpeed = along * (1f + w.SlipRatio) / w.Radius;
        }
    }

    // ── Held to a line ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The wheels of a body whose motion is given rather than worked out: a car locked to its racing
    /// line, or a shuttle. From the speed, the accelerations in the plane of the road (forward and
    /// rightward) and the one pressing it into the road, every wheel's load; the lateral force each
    /// axle group must make to hold the corner, by moments about the centre of gravity (front a
    /// share b / L, rear a / L), shared across each axle by load; the longitudinal force as in
    /// <see cref="Step"/>; and the slip each tyre needs for its force.
    /// </summary>
    public void Hold(float speed, float ax, float ay, float normal = G)
    {
        Vx = speed; Vy = 0f; YawRate = 0f;
        Wet();
        Ax = ax; Ay = ay;
        SteerAngle = 0f;
        TickYaw = TickForward = TickRight = 0f;
        Loads(ax, ay, normal);

        float fyTotal = Mass * ay, fxTotal = Mass * ax;
        float frontLoad = 0f, rearLoad = 0f, total = 0f, driven = 0f;
        foreach (var w in Wheels)
        {
            if (w.Front) frontLoad += w.Load; else rearLoad += w.Load;
            total += w.Load;
            if (w.Driven) driven++;
        }
        float frontFy = _frontAxles > 0 && _rearAxles > 0 ? fyTotal * B / (A + B) : fyTotal * 0.5f;
        float rearFy = fyTotal - frontFy;
        for (int i = 0; i < Wheels.Length; i++)
        {
            ref var w = ref Wheels[i];
            w.SteerAngle = 0f;
            float groupLoad = w.Front ? frontLoad : rearLoad;
            float fy = groupLoad > 0f ? (w.Front ? frontFy : rearFy) * w.Load / groupLoad : 0f;
            float fx = fxTotal >= 0f
                ? (driven > 0f ? (w.Driven ? fxTotal / driven : 0f) : (total > 0f ? fxTotal * w.Load / total : 0f))
                : (total > 0f ? fxTotal * w.Load / total : 0f);
            if (w.Load <= 1f)
            {
                w.Fx = w.Fy = w.SlipAngle = w.SlipRatio = 0f;
                w.Demand = MathF.Abs(fy) + MathF.Abs(fx) > 1f ? 2f : 0f;
                w.AngularSpeed = speed / w.Radius;
                continue;
            }
            float peak = Mu(w) * w.Load;
            float use = MathF.Sqrt(fx * fx + fy * fy) / peak;
            float k = CorneringStiffness(w.Load, w.StaticLoad);
            float b = k / MathF.Max(1e-3f, Tyre.LateralShape * peak);
            float scale = use > 1f ? 1f / use : 1f;
            w.Fx = fx * scale; w.Fy = fy * scale;
            // The slip angle that makes this force; past the peak, the slip grows with the excess.
            float t = use > 1f ? MathF.Sign(fy) * _lat.PeakT * use : _lat.Inverse(fy / peak);
            w.SlipAngle = -t / MathF.Max(1e-3f, b);
            w.SlipRatio = use > 1f ? Math.Clamp(MathF.Sign(fx) * use * 0.1f, -1f, 1f) : SlipRatio(w, fx, peak);
            w.Demand = use;
            w.AngularSpeed = speed * (1f + w.SlipRatio) / w.Radius;
        }
    }

    private static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
