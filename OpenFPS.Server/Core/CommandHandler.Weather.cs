using System.Globalization;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Systems;
using OpenFPS.Words;

namespace OpenFPS.Server.Core;

/// <summary>
/// /weather: what the weather is, and setting it for testing. The weather is the whole server's, so
/// setting it is a staff command (Permissions).
///
///     /weather                        the weather and the wind now
///     /weather clear|rain|snow|storm  that front, held until /weather auto
///     /weather rain heavy             rain at a rate: light, moderate, heavy, violent, or mm/h
///     /weather wind 8 northwest       the wind, held; a direction and steady/gusty/very gusty optional
///     /weather auto                   back to fronts rolling in on their own
///
/// The answer is one or two short plain sentences: a screen reader reads it.
/// </summary>
public partial class CommandHandler
{
    private const string WeatherUsage =
        "Usage: /weather, /weather clear, rain, snow or storm, /weather drizzle, /weather rain light, moderate, heavy or extreme, or a rate in millimetres an hour, or dBZ, and drops then a size, /weather freezing rain, /weather sleet, /weather snow light, moderate or heavy, /weather hail pea, marble, quarter, golf or baseball, /weather wind SPEED [DIRECTION] [steady, gusty or very gusty], or /weather auto.";

    private void HandleWeather(UserSession session, string[] args, Action<IMessage> reply)
    {
        var env = _server.WorldEnvironment;
        if (args.Length == 0)
        {
            // The sky over your map, and what is falling now as well as the front: a front that has just
            // cleared leaves its rain falling for a minute or two (Cody, 2026-10-08: rain heard under
            // "Clear").
            var atmosphere = _maps.TryGetMapData(session.CurrentMapId, out var map) ? MapAtmosphere.Of(map) : MapAtmosphere.Default;
            var now = env.GetStateForMap(atmosphere);
            var falling = env.HeldPrecipitation ?? env.PrecipitationFor(now);
            string front = ScenarioWord(env.CurrentScenario);
            if (env.HeldPrecipitation == null)
            {
                if (env.CurrentScenario == WeatherType.Clear && falling.Falling) front = "Clearing";
                else if (env.CurrentScenario != WeatherType.Clear && !falling.Falling) front += " coming in";
            }
            string held = env.Pinned ? " Held until /weather auto." : " It changes on its own.";
            if (falling.Falling) held = " " + DescribePrecipitation(falling) + held;
            string turning = atmosphere.HeldWeather == null ? DescribeTurning(now.WindVelocity, env.TargetWind) : "";
            Say(reply, $"{front}. {DescribeWind(now.WindVelocity, now.WindGustiness)}{turning} " +
                       $"{now.Temperature.ToString("F0", CultureInfo.InvariantCulture)} degrees.{held}");
            return;
        }

        string first = args[0].ToLowerInvariant();
        if (first == "auto")
        {
            env.Unpin();
            Say(reply, "The weather changes on its own again.");
            return;
        }

        if (TryHandlePrecipitation(first, args, env, reply)) return;

        if (Enum.TryParse<WeatherType>(first, ignoreCase: true, out var scenario) && !int.TryParse(first, out _))
        {
            env.PinScenario(scenario);
            _server.BroadcastEnvironment();
            Say(reply, $"{ScenarioWord(scenario)} coming in. {DescribeWind(env.TargetWind, env.TargetGustiness)}");
            return;
        }

        if (first == "wind")
        {
            if (!TryParseWind(args.AsSpan(1), env, out var velocity, out float gustiness, out string? error))
            {
                Say(reply, error ?? WeatherUsage);
                return;
            }
            env.PinWind(velocity, gustiness);
            _server.BroadcastEnvironment();
            Say(reply, DescribeWind(velocity, gustiness));
            return;
        }

        Say(reply, WeatherUsage);
    }

    private static string ScenarioWord(WeatherType w) => w switch
    {
        WeatherType.Clear => "Clear",
        WeatherType.Rain => "Rain",
        WeatherType.Snow => "Snow",
        WeatherType.Storm => "Storm",
        _ => w.ToString(),
    };

    /// <summary>"Wind 8 metres a second from the north west, gusty." or "Calm."</summary>
    internal static string DescribeWind(Vector3 velocity, float gustiness)
    {
        float speed = new Vector2(velocity.X, velocity.Z).Length();
        if (speed < 0.5f) return "Calm.";
        var air = new WindAir(velocity.X, velocity.Z, 0f, 0, 0, 0);
        string amount = speed >= 3f ? MathF.Round(speed).ToString("F0", CultureInfo.InvariantCulture)
                                    : speed.ToString("F1", CultureInfo.InvariantCulture);
        string unit = amount == "1" ? "metre" : "metres";
        return $"Wind {amount} {unit} a second from the {CompassWord(air.FromDegrees)}, {GustWord(gustiness)}.";
    }

    /// <summary>
    /// " Turning to come from the west." when the wind is on its way round to a direction more than half a point of
    /// the compass off where it blows from now (a front arriving, or the day's turn when it has far to go), else
    /// nothing: the wind changes direction only with the weather, so the weather says where it is going.
    /// </summary>
    internal static string DescribeTurning(Vector3 now, Vector3 target)
    {
        if (new Vector2(now.X, now.Z).Length() < 0.5f || new Vector2(target.X, target.Z).Length() < 0.5f) return "";
        float from = new WindAir(now.X, now.Z, 0f, 0, 0, 0).FromDegrees, to = new WindAir(target.X, target.Z, 0f, 0, 0, 0).FromDegrees;
        float apart = MathF.Abs(((to - from) % 360f + 540f) % 360f - 180f);
        return apart > 22.5f && CompassWord(to) != CompassWord(from) ? $" Turning to come from the {CompassWord(to)}." : "";
    }

    internal static string GustWord(float gustiness) => gustiness < 0.2f ? "steady" : gustiness < 0.5f ? "gusty" : "very gusty";

    /// <summary>The nearest of the eight points to a bearing; exactly between two, the cardinal.</summary>
    internal static string CompassWord(float degrees)
    {
        // Wrapped in float, as it always was: the wrap rounds a bearing a float's width off a midpoint onto it.
        return CompassWords.Of(((degrees % 360f) + 360f) % 360f, 8);
    }

    /// <summary>Each of the sixteen points run together ("northnortheast") and by its initials ("nne"),
    /// to its bearing.</summary>
    internal static readonly Dictionary<string, float> Bearings = BearingsOf();

    private static Dictionary<string, float> BearingsOf()
    {
        var bearings = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < CompassWords.Sixteen.Length; i++)
        {
            string[] words = CompassWords.Sixteen[i].Split(' ');
            bearings[string.Concat(words)] = i * 22.5f;
            bearings[string.Concat(words.Select(w => w[0]))] = i * 22.5f;
        }
        return bearings;
    }

    /// <summary>
    /// "8", "8 northwest", "8 north west gusty", "12 270 very gusty", "0": a speed in metres a second,
    /// where it blows from (a compass point or degrees; the present direction if not said), and how
    /// gusty (as it is if not said). "calm" alone is no wind.
    /// </summary>
    internal static bool TryParseWind(ReadOnlySpan<string> args, WorldEnvironmentSystem env,
                                      out Vector3 velocity, out float gustiness, out string? error)
    {
        velocity = default; error = null;
        gustiness = env.TargetGustiness;
        var words = new List<string>();
        foreach (var a in args) words.Add(a.ToLowerInvariant().Replace(",", ""));
        if (words.Count == 0) { error = WeatherUsage; return false; }

        float speed;
        if (words[0] == "calm") { speed = 0f; words.RemoveAt(0); }
        else if (float.TryParse(words[0].Replace("m/s", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out speed)
                 && float.IsFinite(speed) && speed >= 0f)
            words.RemoveAt(0);
        else { error = $"{args[0]} is not a wind speed. Say it in metres a second, from 0 to 40."; return false; }
        if (speed > 40f) { error = "The strongest wind I will set is 40 metres a second."; return false; }

        // How gusty: the last word or two.
        if (words.Count >= 2 && words[^2] == "very" && words[^1] == "gusty") { gustiness = 0.8f; words.RemoveRange(words.Count - 2, 2); }
        else if (words.Count >= 1 && words[^1] is "verygusty" or "stormy") { gustiness = 0.8f; words.RemoveAt(words.Count - 1); }
        else if (words.Count >= 1 && words[^1] == "gusty") { gustiness = 0.4f; words.RemoveAt(words.Count - 1); }
        else if (words.Count >= 1 && words[^1] == "steady") { gustiness = 0.1f; words.RemoveAt(words.Count - 1); }

        // Where from: the rest, run together ("north west" is "northwest"), or degrees.
        float from;
        words.RemoveAll(w => w is "from" or "the");
        string joined = string.Concat(words), where = joined.Replace("-", "");
        if (where.Length == 0)
        {
            var target = env.TargetWind;
            from = new Vector2(target.X, target.Z).Length() > 0.01f
                ? new WindAir(target.X, target.Z, 0f, 0, 0, 0).FromDegrees
                : 270f;
        }
        else if (Bearings.TryGetValue(where, out float b)) from = b;
        else if (float.TryParse(joined, NumberStyles.Float, CultureInfo.InvariantCulture, out float deg) && float.IsFinite(deg))
            from = ((deg % 360f) + 360f) % 360f;
        else { error = $"I do not know the direction {string.Join(" ", words)}. Say a compass point, like north west, or degrees."; return false; }

        var air = WindAir.FromCompass(speed, from, 0f);
        velocity = new Vector3(air.East, 0f, air.North);
        return true;
    }
}
