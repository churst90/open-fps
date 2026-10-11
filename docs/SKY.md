# The sun and the moon

Cody, 2026-10-11: "Is there way we can add an ability for the player to know where in the sky the sun/moon
are?" The decision is in docs/REAL_WEATHER.md, "Decided (Cody, 2026-10-11)": a readout key and `/sky` say
each body's bearing and height, below the horizon "down, rises at ...". Not a sound in the sky. The key, the
command and the world's real clock come with the weather work's stage 1. What is built is the calculation
and the sentence, in `OpenFPS.Astronomy`.

## Where it lives

`OpenFPS.Astronomy` references no other project and no package. Nothing of it is on the wire, so it is in
neither the wire hash nor the sound library's boundary ratchet (it is not part of the sound library). The
server's `/sky` and the client's key can both reference it; each computes from the latitude, longitude and
UTC instant it has, so they say the same thing.

- `Sun`: position, `Day` (rise, set, solar noon, civil, nautical and astronomical dawn and dusk in one local
  day), `Next` (the next crossing of any altitude after an instant).
- `Moon`: position (topocentric), `Day` (rise and set), `Next`, `IsUp`, `NextPhase`, and the phase: phase
  angle, illuminated share, waxing or waning, and the name.
- `Sky.At`: both at once, with whether each is up and when that changes. `Sky.Readout` and
  `SkyWords.Readout`: the sentence.

## The methods

- The sun: NOAA's solar position equations, as in its solar calculator and spreadsheet (from Meeus), with
  NOAA's refraction formula. Like NOAA's calculator it takes UT for dynamical time; the 69 s between them
  move the sun 0.001 degree.
- The moon: Meeus, Astronomical Algorithms (2nd ed.), chapter 47, the 60 longitude and distance terms and
  60 latitude terms of ELP-2000/82 (about 10 arcseconds), nutation by chapter 22's short form, and the
  topocentric parallax of chapter 40 on the ellipsoid at sea level. The parallax is up to a degree: without
  it the moon on the horizon would be a degree too high. Refraction as for the sun. TT minus UT is held at
  69.2 s for 2015 to 2035, which is what IERS measured; elsewhere Espenak and Meeus's polynomials.
- Rise and set: when the altitude crosses the level, sampled every ten minutes and halved to a second. A
  sample nearer the level than both its neighbours is searched for its turn, so a dip shorter than ten
  minutes (the sun grazing the horizon near the polar circles) is not missed. The levels are the Naval
  Observatory's: the sun's centre 0.833 degree below the airless horizon; the moon's upper limb with 34
  arcminutes of refraction; twilights at 6, 12 and 18 degrees. Polar day and night give null, never NaN,
  and `Next` looks a year ahead for the sun and forty days for the moon.
- The phase name follows the Naval Observatory's, worked out from its answers: by the lit share to the
  nearest percent, new at 0, full at 100, a quarter at 50, a crescent below half and gibbous above. So new
  and full each last about sixteen hours either side of the instant, and a quarter about an hour.

## The sentence

"The sun west south west, twenty degrees up, sets in an hour and ten minutes; the moon waxing gibbous,
three quarters lit, south east, thirty-five degrees up, sets in five hours."

- Bearings are the sixteen points, spelt as the game's eight are ("north east", as `/wind` and the editor
  say them), the half-points by the same rule ("west south west"). The tracker's own words
  (`DirectionWords`, "left in front") are relative to where you face and live in OpenFPS.Common, which this
  library cannot reference; `Readout` takes a bearing function, so the client can pass one that says the
  tracker's words instead. The words are `OpenFPS.Words`' `CompassWords.Of` (16, 8 or 4 points), which
  every compass word in the game now calls; the 8-point copies that were written seven times are gone
  (`CompassWordsTests` keeps them as the record). A test holds the eight main points to the weather's.
- Heights in words to the degree: "one degree up", "on the horizon", "straight overhead".
- Up: "sets in ..." in words. Down: "down, rises at 6:42 a.m.", a clock time in the place's zone. More than a
  day away: "rises on January 15 at 11:35 a.m.".
- The lit share to the nearest eighth ("barely lit" and "fully lit" at the ends), left out at new, full and
  the quarters, where the name says it.

## Accuracy

Checked against published values only, fetched by `tools/astronomy/fetch_references.py` into
`OpenFPS.Tests/Astronomy/References/` (each file names its URLs), for Magnolia TX (30.21 N, 95.75 W),
Albany OR (44.64 N, 123.10 W) and Tromsø (69.65 N, 18.96 E), on the 2026 equinoxes and solstices, the full
moons of 29 June and 26 October, the new moon of 10 October, and 11 October.

| What | Reference | Checked | Tolerance | Worst |
|---|---|---|---|---|
| Sun azimuth and elevation | NOAA's solar calculator, its own functions run on the same instants | 57 instants | 0.05 degree | 0.0000 (the same equations: a transcription check) |
| Sun azimuth and airless elevation | Naval Observatory, celestial navigation data | 57 instants | 0.05 degree | 0.013 azimuth, 0.010 elevation |
| Moon azimuth and topocentric elevation | Naval Observatory, celestial navigation data (hc less the parallax) | 46 instants above the horizon | 0.2 degree | 0.009 azimuth, 0.003 elevation, 0.028 refracted |
| Sunrise, sunset, civil, nautical, astronomical twilight | Naval Observatory year tables for 2026 | every event of the year at all three places, both ways (theirs in ours and ours in theirs): 7,678 | 2 minutes | 0.6 minutes, Tromsø's sun 1.9 |
| Moonrise and moonset | Naval Observatory year tables for 2026 | 1,787 events | 2 minutes | 0.53 minutes |
| Rise, set, civil twilight, transit, one day | Naval Observatory one-day data | 24 place-days | 2 minutes | 0.55 minutes |
| Illuminated share and phase name | Naval Observatory one-day data (at local noon) | 24 place-days | 1 percentage point | 0.49 (the service rounds to whole percent); every name the same |
| The four phases' instants | Naval Observatory phases of 2026 | 50 phases | 2 minutes | 1.93 minutes, mostly the service's rounding to the minute and NOAA's 0.01 degree in the sun's longitude |

Two events are left out of the year comparison: Tromsø's astronomical night of 16 September, five
minutes long in the Naval Observatory's table. The sun dips 0.001 degree below 18 degrees there, a tenth of
either model's accuracy, so whether that night happens is no test of either. The test leaves out any rise
with a set within ten minutes of it, and says how many.

The Meeus example 47.a (1992 April 12) is reproduced too, which catches a mistyped term of the series.

## Not done

- The readout key and `/sky`, and the world's real clock: with the weather's stage 1 (todo.md).
- Height above sea level: rise and set are for sea level, as the Naval Observatory gives them. A hill of
  300 m brings sunrise about two minutes earlier.
- What the sun's height changes in the sound (temperature, the wind's daily turn, the dawn chorus,
  insects): the weather's stages.
