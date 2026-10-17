using OpenFPS.Materials;

namespace OpenFPS.Common;

/// <summary>
/// The air's layers near the ground: how the temperature and the wind change with height, from the sun, the cloud, the
/// hour and the wind over land, and from the air against the water over open water. What bends sound (Refraction) reads
/// this; it is weather, worked out from what the server sends, and holds no sound of its own.
/// </summary>
/// <remarks>
/// <para>Monin-Obukhov similarity: the surface layer's wind and temperature each go as a log of the height plus, in
/// stable air, a term linear in it, set by the friction velocity u*, the temperature scale θ* and the Obukhov length
/// L = u*² T / (κ g θ*), with Businger's profile functions (Businger, Wyngaard, Izumi and Bradley, J. Atmos. Sci. 28,
/// 1971: φm = 1 + 4.7 z/L and φh = 0.74 + 4.7 z/L stable, (1 − 15 z/L)^-1/4 unstable). That is the form Harmonoise's
/// meteorological module takes the lin-log profile's two coefficients from (Heimann and Salomons, Applied Acoustics
/// 65, 2004).</para>
///
/// <para>Over land the heat the ground gives the air comes from the net radiation (RoadWaterLaw.NetRadiationWm2: the
/// sun's height by the hour, the day and the latitude, cut by cloud) by Holtslag and van Ulden's scheme for well-watered
/// grass (J. Clim. Appl. Meteorol. 22, 1983: H = (γ/s)/(1 + γ/s) (Q* − G) − 20 W/m², G a tenth of Q*), and at night
/// from van Ulden and Holtslag's temperature scale, θ* = 0.09 (1 − 0.5 N²) K, N the cloud cover (J. Clim. Appl.
/// Meteorol. 24, 1985). Over open water it comes from the air against the water by the bulk formula,
/// H = ρ cp C_H U (T_water − T_air), C_H 1.13e-3 when the water is warmer and 0.66e-3 when it is colder (Large and
/// Pond, J. Phys. Oceanogr. 12, 1982).</para>
/// </remarks>
public sealed record AirLayers(float TemperatureC, float WindSpeed, float Gustiness, AirLayers.Layer Land, AirLayers.Layer Water)
{
    /// <summary>
    /// One surface's layer. The effective speed of sound along a way the wind blows <c>toward</c> m/s down (its
    /// ten-metre wind's share along the way) is c0 + Log ln(1 + z/z0) + Linear z, with
    /// Log = <see cref="ThermalLog"/> + toward × <see cref="WindLog"/> and
    /// Linear = <see cref="ThermalLinear"/> + toward × <see cref="WindLinear"/>.
    /// </summary>
    /// <param name="ThermalLog">The temperature's log term as speed of sound, m/s: (c/2T) 0.74 θ*/κ.</param>
    /// <param name="ThermalLinear">The temperature's linear term as speed of sound, per second: the adiabatic lapse,
    /// −(c/2T) g/cp, and in stable air (c/2T) 4.7 θ*/(κL).</param>
    /// <param name="WindLog">The wind's log term per m/s of ten-metre wind: (u*/κ) / U.</param>
    /// <param name="WindLinear">The wind's linear term per m/s of ten-metre wind, per metre: 4.7 (u*/κ) / (U L) in
    /// stable air, else zero.</param>
    /// <param name="ObukhovMetres">L: negative unstable, positive stable, infinite neutral.</param>
    /// <param name="FrictionVelocity">u*, m/s: zero in still air.</param>
    /// <param name="TemperatureScale">θ*, K: negative when the ground heats the air, positive when it cools it.</param>
    public readonly record struct Layer(float ThermalLog, float ThermalLinear, float WindLog, float WindLinear, float ObukhovMetres,
                                        float FrictionVelocity, float TemperatureScale)
    {
        /// <summary>
        /// The air's turbulent fluctuations <paramref name="heightMetres"/> over the surface, by surface-layer similarity.
        /// The wind's along and across the mean are 2.4 u* and 1.9 u* (Panofsky and Dutton, "Atmospheric Turbulence",
        /// 1984, ch. 7; WindField.CrossShare is their ratio) at any stability: they follow the boundary layer's depth
        /// rather than the height, and those large eddies bend sound rather than scatter it. The vertical is 1.25 u*,
        /// growing as (1 − 3 z/L)^(1/3) in unstable air (Panofsky, Tennekes, Lenschow and Wyngaard, Boundary-Layer
        /// Meteorol. 11, 1977). The temperature's is 2 |θ*| in neutral and stable air and 2 |θ*| (1 + 9.5 |z/L|)^(-1/3)
        /// in unstable air, whose free-convection limit is Tillman's 0.95 |θ*| (−z/L)^(-1/3) (Kaimal and Finnigan,
        /// "Atmospheric Boundary Layer Flows", 1994, ch. 1; Tillman, J. Appl. Meteorol. 11, 1972).
        /// </summary>
        public Eddies EddiesAt(float heightMetres)
        {
            float zl = float.IsFinite(ObukhovMetres) && ObukhovMetres != 0f ? MathF.Max(0.01f, heightMetres) / ObukhovMetres : 0f;
            float up = UpPerFriction * FrictionVelocity, temperature = TemperaturePerScale * MathF.Abs(TemperatureScale);
            if (zl < 0f)
            {
                up *= MathF.Cbrt(1f - 3f * zl);
                temperature /= MathF.Cbrt(1f - 9.5f * zl);
            }
            return new Eddies(AlongPerFriction * FrictionVelocity, AlongPerFriction * WindField.CrossShare * FrictionVelocity,
                              up, temperature);
        }

        /// <summary>The profile's log coefficient (m/s) and linear coefficient (per second) along a way the ten-metre
        /// wind blows <paramref name="toward"/> m/s down.</summary>
        public (float Log, float Linear) Along(float toward)
            => (ThermalLog + toward * WindLog, ThermalLinear + toward * WindLinear);
    }

    /// <summary>The day of the year to take when a server does not say: the March equinox (day 80), the sun at its
    /// year's middle height.</summary>
    public const int EquinoxDay = 80;

    /// <summary>The surface layer's wind fluctuation along the mean and up, and its temperature fluctuation, over u* and
    /// over |θ*| in neutral air (Panofsky and Dutton 1984; Kaimal and Finnigan 1994; see <see cref="Layer.EddiesAt"/>).</summary>
    public const float AlongPerFriction = 2.4f, UpPerFriction = 1.25f, TemperaturePerScale = 2f;

    /// <summary>
    /// The air's turbulent fluctuations at a height, one standard deviation each: the wind along the mean wind, across it
    /// and up, m/s, and the temperature, K (<see cref="Layer.EddiesAt"/>).
    /// </summary>
    public readonly record struct Eddies(float Along, float Across, float Up, float TemperatureK);

    /// <summary>Von Kármán's constant.</summary>
    public const float Karman = 0.4f;

    /// <summary>Air's specific heat at constant pressure, J/(kg K) (dry air).</summary>
    private const float Cp = 1005f;

    private const float Gravity = Earth.StandardGravity;

    /// <summary>The height the wind is given at, m: the server's wind is the ten-metre wind (WindField).</summary>
    private const float WindHeight = 10f;

    /// <summary>
    /// The air with no sun, no cloud and no stir: the adiabatic lapse alone (neutral), what a world has before the
    /// server's first word.
    /// </summary>
    public static AirLayers Neutral(float temperatureC = 15f)
    {
        var layer = NeutralLayer(temperatureC);
        return new AirLayers(temperatureC, 0f, 0f, layer, layer);
    }

    /// <summary>
    /// The layers for the server's weather: the hour and day of the year (the sun), the latitude, the precipitation and
    /// humidity (the cloud, RoadWaterLaw.CloudFrom), the air's temperature and pressure, and the ten-metre wind's speed
    /// and gustiness.
    /// </summary>
    public static AirLayers From(float hour, int dayOfYear, float latitudeDegrees, float precipitationIntensity, float humidity,
                                 float temperatureC, float pressureMb, float windSpeed, float gustiness)
    {
        float cloud = RoadWaterLaw.CloudFrom(precipitationIntensity, humidity);
        float netRadiation = RoadWaterLaw.NetRadiationWm2(hour, dayOfYear, cloud, latitudeDegrees);
        var land = OverLand(netRadiation, cloud, temperatureC, pressureMb, windSpeed);
        var water = OverWater(WaterTemperatureC(dayOfYear), temperatureC, pressureMb, windSpeed);
        return new AirLayers(temperatureC, windSpeed, gustiness, land, water);
    }

    /// <summary>
    /// The land's layer for a net radiation (W/m², negative at night), a cloud cover (0..1), the air's temperature and
    /// pressure and the ten-metre wind.
    /// </summary>
    public static Layer OverLand(float netRadiationWm2, float cloud, float temperatureC, float pressureMb, float windSpeed)
    {
        float t = temperatureC + 273.15f;
        float rhoCp = Density(temperatureC, pressureMb) * Cp;
        // The Priestley-Taylor share of the available energy that heats the air rather than evaporates water, for a
        // moist surface (α = 1): (γ/s) / (1 + γ/s), s the saturation curve's slope (Tetens) and γ the psychrometric constant.
        float es = RoadWaterLaw.SaturationHpa(temperatureC);
        float s = 4098f * es / ((temperatureC + 237.3f) * (temperatureC + 237.3f));
        float share = 0.66f / s / (1f + 0.66f / s);
        float nightTheta = 0.09f * (1f - 0.5f * Math.Clamp(cloud, 0f, 1f) * Math.Clamp(cloud, 0f, 1f));
        return Solve(temperatureC, windSpeed, ustar =>
            netRadiationWm2 > 0f ? -(share * 0.9f * netRadiationWm2 - 20f) / (rhoCp * ustar) : nightTheta, t);
    }

    /// <summary>The open water's layer for the water's and the air's temperatures, the air's pressure and the ten-metre
    /// wind. Air warmer than the water lies on it stably and bends sound down.</summary>
    public static Layer OverWater(float waterC, float temperatureC, float pressureMb, float windSpeed)
    {
        float diff = waterC - temperatureC;
        float ch = diff > 0f ? 1.13e-3f : 0.66e-3f;
        float u = MathF.Max(WindField.MinimumCarryMetresPerSecond, windSpeed);
        // θ* = −H / (ρ cp u*) = −C_H U (T_water − T_air) / u*.
        return Solve(temperatureC, windSpeed, ustar => -ch * u * diff / ustar, temperatureC + 273.15f);
    }

    /// <summary>
    /// The temperature of open water on a day of the year, °C: a lake's mixed layer following the server's seasonal air
    /// (15 °C, 20 either side, warmest at day 172) about a month behind and with a little less swing, as a body of water
    /// a few metres deep does (Edinger, Duttweiler and Geyer, Water Resources Research 4, 1968: a response time of
    /// weeks). No daily swing; never below freezing (ice is not water). A judgement.
    /// </summary>
    public static float WaterTemperatureC(int dayOfYear)
        => MathF.Max(0.5f, 15f + 18f * MathF.Cos((dayOfYear - 200) / 365f * 2f * MathF.PI));

    /// <summary>The speed of sound in air at a temperature, m/s (Air.SpeedOfSound).</summary>
    public static float SpeedOfSound(float temperatureC) => new Air(temperatureC, Air.Standard.PressureMb).SpeedOfSound;

    private static float Density(float temperatureC, float pressureMb) => new Air(temperatureC, MathF.Max(500f, pressureMb)).Density;

    private static Layer NeutralLayer(float temperatureC)
    {
        float t = temperatureC + 273.15f;
        float lapse = -SpeedOfSound(temperatureC) / (2f * t) * Gravity / Cp;
        return new Layer(0f, lapse, 1f / MathF.Log(WindHeight / WindField.RoughnessMetres), 0f, float.PositiveInfinity, 0f, 0f);
    }

    /// <summary>
    /// The surface layer for a temperature scale given as a function of u*: u* and L found together (four rounds settle
    /// it), z/L at ten metres held to at most 1 (where Businger's stable forms were measured; Högström,
    /// Boundary-Layer Meteorol. 42, 1988), and to at least −10 unstable.
    /// </summary>
    private static Layer Solve(float temperatureC, float windSpeed, Func<float, float> thetaStar, float t)
    {
        float z0 = WindField.RoughnessMetres;
        float u = MathF.Max(WindField.MinimumCarryMetresPerSecond, windSpeed);
        float logZ = MathF.Log(WindHeight / z0);
        float ustar = Karman * u / logZ, theta = 0f, l = float.PositiveInfinity;
        for (int i = 0; i < 4; i++)
        {
            theta = thetaStar(ustar);
            l = MathF.Abs(theta) < 1e-6f ? float.PositiveInfinity : ustar * ustar * t / (Karman * Gravity * theta);
            if (l > 0f && l < WindHeight) l = WindHeight;
            if (l < 0f && l > -WindHeight / 10f) l = -WindHeight / 10f;
            ustar = Karman * u / MathF.Max(1f, logZ - Psi(WindHeight / l) + Psi(z0 / l));
        }
        float ct = SpeedOfSound(temperatureC) / (2f * t);
        float lapse = -ct * Gravity / Cp;
        bool stable = l > 0f && float.IsFinite(l);
        float thermalLog = ct * 0.74f * theta / Karman;
        float thermalLinear = lapse + (stable ? ct * 4.7f * theta / (Karman * l) : 0f);
        float windLog = ustar / Karman / u;
        float windLinear = stable ? 4.7f * windLog / l : 0f;
        return new Layer(thermalLog, thermalLinear, windLog, windLinear, l, ustar, theta);
    }

    /// <summary>The momentum profile's departure from the log law at z/L (Businger et al. 1971; Paulson's integral of
    /// the unstable form, J. Appl. Meteorol. 9, 1970).</summary>
    private static float Psi(float zOverL)
    {
        if (!float.IsFinite(zOverL) || zOverL == 0f) return 0f;
        if (zOverL > 0f) return -4.7f * zOverL;
        float x = MathF.Pow(1f - 15f * zOverL, 0.25f);
        return 2f * MathF.Log((1f + x) / 2f) + MathF.Log((1f + x * x) / 2f) - 2f * MathF.Atan(x) + MathF.PI / 2f;
    }
}
