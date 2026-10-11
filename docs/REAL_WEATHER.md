# Real weather for real places

Cody, 2026-10-10: drive the world's weather from real weather data for the real places in it. This is the
design only; nothing here is built. Written against main at cf82a7d9. It sits on docs/MATTER.md (5.3 and the
order of work, steps 5 and 6: weather as a system, wind acting on things), CODY_ASKS_2026-10-08 item 12
(weather as a system, every value holdable by hand), docs/WORLD_STREAMING.md (one world on the UTM grid,
frames, the server fetches) and todo.md ("Real places from open data": "Real weather for real places
(Open-Meteo or NOAA by latitude and longitude)"). Its build order stays where todo.md puts weather: Next, in
order, item 7 (the detail, after the physics and library stages).

Every claim about a source was checked against that source's own pages or by asking it on 2026-10-10;
the URLs are in section 13. Figures marked [recalled] were not checked here.

Where it stands (2026-10-11): a design, nothing built. Decided by Cody: the world's clock is the real local
time at real places (section 12, decision 2). Planned the same day: where the sun and the moon are for the
player, with the clock in stage 1 (section 12; docs/SKY.md). Open: the other six decisions in section 12
(Open-Meteo's free tier, the User-Agent and on-demand fetching, made-up maps, real lightning, the NWS API,
when the data stops).

## Contents

1. The rule
2. What exists today
3. Sources
4. Which sources, and why
5. Mapping real fields onto the simulation
6. Space: one fetch covering an area
7. Time: hourly values to continuous weather
8. The server
9. Cody's rules, and how this keeps them
10. Stages and effort
11. Risks
12. Decisions for Cody
13. Sources
14. Not verified

## 1. The rule

Real data is a source of **conditions**, not a replacement for the simulation:

- What a source gives is a few numbers at a point, once an hour or once a quarter: the mean wind, the
  hour's peak gust, the temperature and humidity, the hour's precipitation and what kind. The weather a
  player hears is everything under that: the gust arriving through the trees upwind, the shower cell that
  starts and stops inside the hour, the front that crosses the street at 14:20 and not at the hour mark.
  The simulation makes all of that, and is constrained by the data so that its averages are the data's.
- The weather acts on things, as it does now: the wind field moves leaves, flames and your ears; the rain
  lands on materials (`ParticleSpectrum`, `RainSurfaces`), runs off (`Runoff`, `GroundWater`), wets the
  roads (`RoadWater`) and the fuel (`FireSpread`). Real data changes what goes into those models, never what
  comes out of them. No real field is ever mapped to a sound.
- The same laws everywhere. A place differs from another only by its data and its geometry: no per-place
  tuning, no per-map constants.
- Without data (offline, a failed fetch, a map with no place) the simulated weather carries on from where
  the real one was, with no step.

## 2. What exists today

The weather is one sky for the whole server (`WorldEnvironmentSystem`), overlaid per map, broadcast to each
graphical session once a second (`GameServer.BroadcastEnvironment`, `WorldStateUpdate`).

| Part | Code | What it holds or does |
|---|---|---|
| Fronts | `WorldEnvironmentSystem.SetScenario` | Four fixed states (`WeatherType` Clear, Rain, Snow, Storm), each a set of targets: humidity, precipitation intensity 0..1, a mean wind vector, gustiness 0..1, a temperature offset and ceiling. A new front every 300 s on average (`MeanSecondsBetweenFronts`, Cody 2026-10-09). |
| Easing | `WorldEnvironmentSystem.Update` | Each value lerps toward its target (wind and humidity at 0.05/s, precipitation at 0.02/s; five times faster when set by hand). Rain below a tenth of drizzle stops. |
| Temperature | same | 15 C ± 20 over the year, ± 5 over the day, plus the front's offset. Game time runs 60 times real time (`_timeMultiplier`). |
| Held by hand | `/weather`, `PinScenario`, `PinWind`, `PinPrecipitation`, `OPENFPS_WEATHER` | A front, a wind, or a precipitation (kind, rate, drop size, hail size) held until `/weather auto`. |
| A map's sky | `MapAtmosphere`, `GetStateForMap` | Authored temperature and humidity as offsets, authored air pressure and absorption; an editor map can hold a weather and an hour. |
| Wind | `OpenFPS.Sound/Wind.cs`: `WindField`, `WindAir`, `WindWeather` | The mean ten-metre wind (east, north), a turbulence intensity from the gustiness (`TurbulenceFor`: 0.18 to 0.30), the log law with height (z0 0.15 m), and a frozen-turbulence eddy pattern (five eddy sizes, 180 m to 5 m) carried by the mean wind. Hashed from a lattice and read at `WindField.Now()` (UTC seconds since 2026-01-01), so every client hears the same gust at the same tree. The server keeps the pattern's travel and sends it. |
| Precipitation | `OpenFPS.Sound/Precipitation.cs`, `Rain.cs` | Kind (Rain, FreezingRain, Sleet, Snow, Hail), water-equivalent rate in mm/h, median drop D0 (Marshall-Palmer's for the rate unless set), median hailstone. Intensity 0..1 maps to 0.5 mm/h drizzle at 0.1 and 60 mm/h at 1. Snow below 2 C unless held. |
| Lightning | `LightningSystem`, `OpenFPS.Sound/Lightning.cs` | Cells 8 to 25 km upwind that drift with the ground wind and flash at 4 a minute at peak for Storm, up to 0.3 for heavy Rain; a quarter of flashes to ground. |
| Run-off | `OpenFPS.Sound/Runoff.cs` | A ladder of linear reservoirs (30 s to an hour) fed by the rain on the client. |
| Roads | `RoadWater`, `RoadWaterSystem` | Texture, sheet, gutter and puddles per map, on the server; drying by Penman with the sun at latitude 45 ("maps carry none yet") and cloud read off the humidity ("the weather has no clouds of its own yet"). |
| Ground | `GroundWaterSystem`, `GroundWater` | Run-off over the world tiles' drainage lines and ponds. |
| Fuel | `FireSpread.Moisten` | Dead fuel moves toward Simard's equilibrium moisture from temperature and humidity, with its timelag, or toward 0.35 in rain. |
| Wire | `WorldStateUpdate` | GameTime, Temperature, Humidity, AirPressure, AirAbsorptionMultiplier, WindVelocity, WindGustiness, PrecipitationIntensity, then appended: WindClock, WindTravelEast/North, RainRateMmPerHour, PrecipitationKind, RainMedianDropMm, HailDiameterMm, RoadWater, GroundWater. Positional: append only. |

A parallel piece of work is turning `WindField` and `Runoff` from statics into a per-world weather instance
that the host feeds. This design feeds that instance and calls it **the per-world weather the host feeds**.
It needs nothing more from it than a place to put conditions in.

Built 2026-10-10 (docs/SOUND_LIBRARY_BOUNDARY.md section 18), on the sound side: `WorldWeather`
(`OpenFPS.Sound/WorldWeather.cs`), one per audio world, which fetches nothing. Its members are `Wind` (the
`WindWeather` ramp, swapped whole), `Runoff` (advanced with the rain falling, `Update(rainMmPerHour, now)`),
`Ground` (the server's `GroundWater` state, `Load`), the lab's `MeanSpeed`, `FromDegrees` and `Turbulence`, and
`SpeedAt`. The client fills it from each `WorldStateUpdate` as before, so whatever this design puts in the
broadcast reaches it unchanged; a host with weather of its own sets those members directly.

On the world, each frame (a map made by the server, 6 km each way, `MapData.IsWorld`) is already its own map
id, so the per-map broadcast is already per frame. That is the unit real weather is fed at.

## 3. Sources

### 3.1 The comparison

| | NWS API (api.weather.gov) | Open-Meteo | aviationweather.gov (METAR/SPECI) | MADIS / mesonets |
|---|---|---|---|---|
| Coverage | US only (each forecast office's area) | Worldwide; "Best match" picks the highest-resolution model for each place | Worldwide airports: 5,121 stations in the current-METAR file on 2026-10-10, 2,312 of them in the contiguous US (K), 253 in Africa (D, F, G, H) | US mostly; most mesonet data restricted |
| What it is | Observations (from MADIS) and the forecast offices' gridded forecast | Numerical weather models, interpolated to the point | Observations at airports, hourly plus specials | Quality-controlled observation feeds |
| Fields | Gridpoints: temperature, dew point, RH, sky cover, wind speed, direction and gust, 20-ft wind, weather (type, coverage, intensity), probability of precipitation, QPF, snowfall, ice, visibility, mixing height, transport wind, probability of thunder, lightning activity level. Observations: temperature, dew point, wind, gust, pressure, visibility, present weather, cloud layers | Hourly: temperature, RH, dew point at 2 m; MSL and surface pressure; cloud cover total, low, mid, high; precipitation, rain, showers, snowfall, snow depth, weather code; wind speed and direction at 10, 80, 120, 180 m and on pressure levels; gusts; radiation; CAPE; visibility; freezing level; boundary layer height; soil temperature and moisture. 15-minutely: a subset, including lightning potential | Temperature, dew point, wind direction and speed (kt, or VRB), gust (kt, when reported), visibility, altimeter and SLP, present weather (wx string: DZ, RA, SN, PL, GR, GS, FZRA, TS, BR, FG...), cloud layers, raw text with remarks | Surface obs of many networks |
| Time resolution | Gridpoint values as runs of hours (`validTime` with ISO durations); obs hourly at most stations | Hourly; 15-minutely native in North America (HRRR) and Central Europe, interpolated hourly elsewhere | Hourly (about :53 or :56 in the US), plus SPECI on change | 5 min to hourly |
| Latency | Obs "may lag up to 20 minutes" behind MADIS; gridpoints updated about hourly (`updateTime` 17:04 on a fetch at about 18:15; `max-age` 3,499 s) | "Current" is the present 15-minute slot; models ingested hourly (GFS, HRRR), 3-hourly (ICON) or 6-hourly (ECMWF), stitched to the previous run "without gaps or discontinuities" | Cache files rebuilt every minute; a 17:53 report was received at 17:56 | Real time, for account holders |
| Rate limits | "Not public information"; over the limit, retry "typically within 5 seconds" | Free: 600 a minute, 5,000 an hour, 10,000 a day, 300,000 a month; a request weighs locations × (days ÷ 14) × (variables ÷ 10) | 100 requests a minute; most endpoints return at most 400 entries; use the cache files for bulk | Account based |
| Key | None; a User-Agent that identifies the application and a contact is required ("will be replaced with an API key in the future") | None for the free tier; a key for paid plans | None; a custom User-Agent advised | Application form, account made by the MADIS team |
| Terms and licence | Public domain, "may be used without charge for any lawful purpose"; do not present modified data as official; NWS name and logo not to imply endorsement | Free tier for non-commercial use only; data CC BY 4.0: "a link next to any location Open-Meteo data are displayed" ("Weather data by Open-Meteo.com"); CC BY permits copying, redistributing and adapting | An NWS site; its disclaimer is the weather.gov one (public domain). Foreign METARs pass through it (see section 14) | Unrestricted for non-mesonet surface data; mesonet data must not be redistributed outside its category |
| Storing and redistribution | Allowed (public domain) | Allowed under CC BY 4.0 with the attribution | Allowed for NWS data | Restricted |
| Reliability | "As is"; "timely delivery ... is not guaranteed" | Free tier: "no uptime guarantee"; IPs that misuse it may be blocked "without prior notice" | "Exceeding limits will result in access being blocked" | Operational, but access is the obstacle |
| Historical | Alerts for 7 days; no archive (points to NCEI) | Archive API: ERA5 from 1940 (25 km, 5 days late), ERA5-Land from 1950 (11 km, with gusts, no precipitation), ECMWF IFS from 2017 (9 km, no delay). The free tier includes it (Professional and up among paid plans) | `hours` answered for the last two hours; how far back it goes not checked | Online archive of real-time obs |

### 3.2 Mesonets are not practical

MADIS mesonet data, the dense network that would be most use between airports, is "restricted":
redistribution outside the "full distribution" category is not allowed, and access needs an application
form processed by the MADIS team. The non-mesonet surface data MADIS has unrestricted is the airport data
METAR already gives. Synoptic Data, the main mesonet aggregator, offers free access only to members of
accredited US universities with a .edu address, for non-commercial research, one year at a time. Neither
fits a game server that sends what it derives to every player.

### 3.3 What the sources said on 2026-10-10

Asked between 18:00 and 18:20 UTC, the nearest airport against Open-Meteo at the place:

| | Conroe (KCXO, 17:53) | Open-Meteo at Magnolia (18:15 slot) | Corvallis (KCVO, 17:56) | Open-Meteo at Albany (18:15 slot) |
|---|---|---|---|---|
| Temperature, C | 28.9 | 29.2 | 12.2 | 12.2 |
| Dew point, C | 17.8 | 18.6 | 7.2 | 6.9 |
| Wind | from 110 at 7 kt (3.6 m/s) | from 110 at 2.3 m/s, gusts 3.0 | from 120 at 4 kt (2.1 m/s) | from 195 at 1.1 m/s, gusts 2.8 |
| Pressure, hPa | SLP 1016.9 | MSL 1014.9, surface 1005.5 | SLP 1012.8 | MSL 1011.5 |
| Sky | clear | cloud 1 % | overcast at 6,000 ft | cloud 31 % (Salem: broken at 7,500 ft) |

Conroe is 31 km from Magnolia and Corvallis 22 km from Albany. Temperature and humidity agree to under a
degree; the model's light winds are a third to a half under the airports', and the model missed
Corvallis's overcast. One sample, not a verification, but it is the reason the design corrects the model with
observations (section 6.3). Open-Meteo's 15-minute lightning potential came back empty for both US places:
it is not there in North America.

## 4. Which sources, and why

- **Open-Meteo as the field.** It is the only one of the four that covers the whole world, as the world is
  meant to, and the only one that gives a continuous field (a model at every point) rather than points.
  It has every field the simulation can use (section 5), hourly everywhere and 15-minutely in North
  America and Central Europe, needs no key, and its CC BY 4.0 licence lets the server store what it
  fetched and send what it derives to players with one line of attribution. Its past days (up to 92 on
  the forecast endpoint) and its archive let the game start a place with the ground, the roads and the
  fuel as wet or dry as the real last days made them. The catch is its free tier: non-commercial only
  (decision 1).
- **METAR from aviationweather.gov as the truth near the ground.** Worldwide, public domain for the NWS's
  own, one file for every station in the world rebuilt every minute (about 280 KB compressed), so one
  request every ten minutes serves every player and says nothing about where any of them are. It is the
  only one of the four that says what is happening now (rain now, thunder now, the wind shift that just
  happened), and it corrects the model's biases (the light winds above).
- **The NWS API: not at first.** Its gridded forecast is the best for the US (2.5 km, edited by
  forecasters, with a probability of thunder and a lightning activity level), but it is US only, its
  observations are the same airports as METAR arriving up to 20 minutes later and one request per station,
  and it asks for a User-Agent that names a contact. It is a later option for thunder in the US
  (stage 6), not a foundation.
- **Real lightning, later and optional.** NOAA's Geostationary Lightning Mapper (GLM) flashes are public
  (CC0) in the AWS bucket `noaa-goes19`, one file every 20 seconds (about 310 KB, listed 25 to 30 s after
  each window closed on 2026-10-10), over the Americas only, at about 10 km. That is 1.3 GB a day to read,
  so it is stage 6 and a decision (5).

## 5. Mapping real fields onto the simulation

### 5.1 Fields with a slot today

| Real field | From | Slot | How |
|---|---|---|---|
| Mean wind, speed and direction at 10 m | OM `wind_speed_10m`, `wind_direction_10m` (instantaneous, m/s with `wind_speed_unit=ms`); METAR `wdir`, `wspd` (kt; VRB under about 6 kt [recalled]) | `WindVelocity` → `WindAir` East, North | The from-direction and speed to the east and north the air moves (`WindAir.FromCompass` does it). Interpolated as components, never as an angle (350 to 10 degrees does not go the long way round). VRB keeps the model's direction and takes the speed. |
| Gusts | OM `wind_gusts_10m` (the preceding hour's maximum); METAR `wgst` (only when reported) | `WindGustiness` → turbulence intensity (`TurbulenceFor`) | The game's own relation is gust factor ≈ 1 + 3 TI (the doc comment on `TurbulenceFor`), for a peak in about ten minutes. An hour's peak is higher: for a Gaussian process the expected peak factor is √(2 ln νT) + 0.577/√(2 ln νT) (Davenport 1964 [recalled]), about 3.3 for ten minutes and 3.8 for an hour at ν = 0.2 Hz, so the game's 3 becomes 3 × 3.8 / 3.3 ≈ 3.45 for an hour's peak: TI = (G_hour − 1) / 3.45, then gustiness = (TI − 0.18) / 0.12. A METAR without a gust group means "no gust over the threshold", not "steady": it caps the gust factor, it does not set it. |
| Temperature | OM `temperature_2m`; METAR `temp` | `Temperature` | In the world no map offset applies; corrected from the source's height to the ground's (section 6.2). Replaces the seasonal and daily curve on the world. |
| Humidity | OM `relative_humidity_2m`, `dew_point_2m`; METAR `dewp` | `Humidity` (0..1) | Carried as the dew point, which is conserved when the air warms through the day, and turned into relative humidity at the ground's temperature (Magnus). |
| Pressure | OM `surface_pressure`; METAR altimeter or SLP | `AirPressure` (per map today) | The station pressure at the ground's own height (hypsometric equation from MSL), so air absorption and the speed of sound use the air that is there. |
| Precipitation rate | OM `precipitation` (preceding hour's sum, mm, water equivalent), `rain`, `showers`, `snowfall`; METAR intensity (−, none, +) and the hourly P group (US) | `PrecipitationIntensity` via `Rainfall.IntensityFor`, `RainRateMmPerHour` | The hour's sum is a mean rate; section 7.3 makes it into cells whose average is that rate. Snow water is `precipitation − rain − showers`, so no snow ratio is assumed. The intensity curve tops out at 60 mm/h; real cloudbursts exceed it, so the rate, not the intensity, must be what the systems read. |
| Precipitation type | OM `weather_code` (WMO: 51-57 drizzle, 61-67 rain, 71-77 snow, 80-82 rain showers, 85-86 snow showers, 95-99 thunderstorm; 96 and 99 hail only from models that forecast it, DWD ICON and UKMO); METAR wx (DZ, RA, SN, SG, PL, GR, GS, FZRA, FZDZ) | `PrecipitationKind`, `RainMedianDropMm`, `HailDiameterMm` | Drizzle is Rain with drizzle-sized drops (D0 under 0.5 mm [recalled, AMS Glossary]); freezing drizzle and rain are FreezingRain; PL and GS are Sleet; SG is Snow with small flakes; GR is Hail. Showers take the convective D0 for the rate and steady rain the stratiform one (the split `Precipitation` already cites, Bringi et al. 2003). The 2 C snow line stays only as the fallback when the data does not say. |
| Thunder | OM `weather_code` 95-99, `cape`; METAR TS, LTG remarks; (NWS probability of thunder, lightning activity level; GLM) | `LightningSchedule` via `StormSky` (today keyed on `WeatherType.Storm`) | A cell's formation rate from the convective state (code 95-99 or TS nearby, or CAPE with showers), not from a front's name. `StormSky` gains a convective input; `WeatherType` stays for held weather. |

### 5.2 Fields with no slot yet

| Real field | From | What it would act on |
|---|---|---|
| Cloud cover | OM `cloud_cover` (and low, mid, high); METAR layers | Net radiation for drying (`RoadWater` reads cloud off the humidity today), the night's cooling, dew. |
| Sunlight | OM `shortwave_radiation` | Penman's net radiation directly, and the latitude of the place for the sun (`RoadWater` assumes 45 degrees). |
| Visibility and fog | OM `visibility`, codes 45 and 48; METAR `visib`, BR, FG | Fog drip from trees and eaves, wet surfaces without rain; the marker of a still, stable night. Fog itself barely changes sound. |
| Dew | dew point against the ground's temperature at night | Wet grass and cars at dawn without any rain (the texture of `RoadWater`, the fuel's moisture). |
| Snow on the ground | OM `snow_depth` | Snow and ice underfoot (todo "Weather, the rest": measure before synthesising), grip (`RoadSurfaces` has 0.2 and 0.1 from map materials only). |
| The air's profile | OM temperature and wind at 80, 120, 180 m, `boundary_layer_height`, pressure-level winds (`wind_speed_700hPa` answered) | Sound bending up on a hot afternoon and down on a still night (MATTER 5.2); the steering wind a storm cell drifts with (`LightningSchedule` uses the ground wind, "the only one the server has"). |
| Soil moisture | OM `soil_moisture_0_to_1cm` and deeper | A check on the game's own ground water, not an input (section 5.4). |
| Freezing level | OM `freezing_level_height` | Whether what falls melts on the way down; sleet and freezing rain. |

New wire fields are appended to `WorldStateUpdate` only when something on the client reads them (cloud and
sun for drying run on the server, so they may never need to go out at all).

### 5.3 What real data cannot give, which the simulation must make

- **The gust structure under the mean.** No source has it. `WindField` makes it, as now, from the mean and
  the turbulence intensity, on the shared UTC clock (section 7.2).
- **A front's arrival between updates.** Hourly values blend across an hour; a front crosses a street in
  seconds. Section 7.4.
- **Rain's intermittency.** An hour's 2 mm may be twenty minutes of 6 mm/h and forty dry. Section 7.3.
- **Where the lightning strikes** (outside the Americas, or without GLM): cells placed where the data says
  the convection is, flashing at the physics' rates.
- **Hail size.** No free source gives it. The largest stone a storm can carry is the one whose fall speed
  equals the updraft, and the updraft is about √(2 CAPE) at most (parcel theory [recalled]): with
  `Hydrometeors.FallSpeed` inverted, CAPE sets a ceiling and the hail spectrum is drawn under it.
- **Local roughness.** The model's 10 m wind is over its grid cell's ground. The game's log law uses one
  roughness (0.15 m); the land cover the world tiles already carry (ESA WorldCover) could set it per tile
  (woods and towns about 1 m, grass a few centimetres [recalled]). Not this design's work, but the seam is
  the same.

### 5.4 The ground's wetness

Real soil moisture is a model's own reservoir and does not match the game's (curve numbers, the road
texture, ponds). The game uses its own physics with the real history instead: when a place first gets real
weather, the last days of real precipitation, temperature, humidity, wind and sun (OM `past_days`, up to 92)
are run through `RoadWater`, `GroundWater` and `FireSpread`'s moisture at a few minutes a step. A player
arriving after a night of storms finds full puddles and running drains; after a dry month, fuel at its
equilibrium. 100-hour fuel needs about four days of history and 1,000-hour fuel about six weeks (their
timelags). This replaces `Runoff`'s "settle at the rain's own rate" on the world; a made-up map keeps it.

## 6. Space: one fetch covering an area

### 6.1 A lattice of points, not stations

The model field is fetched at fixed points: a lattice every 0.1 degree of latitude and longitude (about
11 km north-south; 9.6 km east-west at 30 N, 7.9 km at 45 N). Fixed in latitude and longitude, not on the
UTM grid, so it has no zone seams and is the same lattice for every server. It is close to the models'
own spacing (HRRR 3 km, ICON 11 km, GFS 0.25 degree [recalled]); a finer one adds little outside HRRR and
ICON-D2.

A frame (12 km across) overlaps one to four lattice cells, so it needs the nodes round them, 4 by 4 to
5 by 5, and a player near a frame's edge or travelling fast the next ring. The value at a point is the bicubic (Catmull-Rom) interpolation of the 16
nodes round it: continuous with a continuous slope everywhere, so crossing a cell's edge makes no step
and no kink. The node a value came from is never visible.

### 6.2 Heights

Open-Meteo already corrects temperature to a 90 m elevation model at the requested point and says which
elevation it used. The server takes each node's value at that elevation and corrects it to the ground under
the player (the world tiles carry 3DEP heights) by the standard lapse rate (6.5 K/km), the dew point by
its own (about 2 K/km [recalled]) and the pressure by the hypsometric equation. A creek bottom is then a
little warmer by day than the ridge above it, from the geometry, with no per-place figure.

### 6.3 Observations: correcting the field near stations

The model is corrected toward the airports with a Barnes analysis of the residuals [recalled: Barnes 1964]:

1. At each station, the residual is the observation minus the model interpolated to the station's point
   and height (temperature, dew point, wind east, wind north, pressure).
2. The correction at any point is the weighted mean of the residuals of every station within 3L,
   weights exp(−r²/L²), times a confidence that falls to nothing far from every station
   (Σw / (Σw + w0)). L about 40 km; far from airports the model stands alone.
3. A station that appears, disappears or is rejected fades its weight in or out over ten minutes, so
   a missing report never moves the weather at once.
4. Checks before a report is used: plausible ranges, its age (older than 90 minutes is dropped), a
   neighbour check (a residual far from its neighbours' is rejected), and METAR's own quality flags.

The sum of smooth weights is smooth, so no station has a boundary: there is no line where one station
stops and the next begins. Present weather (rain now, thunder now) is used differently: it tells the
simulation that it is raining near the station now (section 7.3), not what the rate is.

## 7. Time: hourly values to continuous weather

### 7.1 Between samples

- Instantaneous fields (temperature, dew point, wind components, pressure, cloud) at each hour (or
  quarter) are joined by a monotone cubic (Fritsch and Carlson 1980 [recalled]): smooth, and never
  overshooting between two samples.
- Hourly sums (precipitation) are means over the hour before the stamp. Each is treated as the rate at
  the middle of its hour and joined linearly, which keeps each hour's total to within a few per cent;
  the cells of 7.3 then put the rain where it falls.
- A new model run or a new fetch changes the series ahead, never the present: the server blends the old
  series into the new over ten minutes from now. Open-Meteo already stitches runs together "without gaps or
  discontinuities".
- The weather the server feeds then eases as today (the lerp rates in `WorldEnvironmentSystem.Update`),
  so even a bad sample arrives as a front would, not as a step.

### 7.2 Gusts stay agreed

Nothing changes in how gusts are agreed. The server feeds the per-world weather a mean wind and a
turbulence intensity per frame; the eddy pattern is carried by the mean, its travel integrated on the server
and sent with `WindClock` (`WindField.Now()`, UTC) as today; each client reads the same pattern at the same
UTC second. Real data changes the mean and the intensity once an hour, slowly, which the one-second ramp of
`WindWeather.Following` already handles.

Two things the world adds:

- **Travel per frame.** Frames hundreds of kilometres apart have different winds; each frame integrates its
  own travel, from its own mean. Within a frame the mean varies a little in space (6.1); the pattern is
  carried at the frame's mean, which is what Taylor's hypothesis assumes anyway.
- **Rebasing.** When a frame moves its origin by whole tiles (stage 3 of the streaming plan), the travel is
  shifted by the same offset in the same step, or every gust would jump.

### 7.3 Showers and steady rain

Real rain comes in cells. The simulation draws rain cells that drift with the steering wind (the 700 hPa
wind, or the 180 m wind if that is missing) across each frame, as `LightningSchedule`'s storm cells already
drift:

- The hour's mean rate R and its convective share (OM `showers` over `rain` + `showers`; METAR SH) choose
  the cells: steady rain is wide and even (cover near one, in-cell rate near R); showers are a few km
  across with a cover of a quarter to a half and an in-cell rate of R over the cover [judgement, to fit
  against radar later].
- The cover times the in-cell rate is R, so the hour's total at any point is the data's on average, and a
  player standing still hears a shower start, pour and stop.
- A METAR reporting rain now near a player, when the cells have none there, places a cell over the station
  (and the reverse: a station reporting no rain pushes the cells off it). The present is the observation's.

### 7.4 A front arriving

A front between two hourly samples should cross the frame as a line, at its real speed, not fade in
everywhere at once.

1. **Motion between hours.** For each pair of hours the server estimates how the field moved: the
   displacement that best matches the wind, temperature and pressure fields at hour h to hour h+1 over the
   nodes round the frame (block matching, as radar nowcasting does [recalled: pySTEPS]). Interpolation then
   follows the motion: the value at a point at time t blends hour h's field moved forward and hour h+1's
   moved back, so a gradient moves across the frame instead of dissolving.
2. **Sharpening.** A model front is two grid cells wide or more (6 to 25 km); a real cold front's wind
   shift at the surface can be well under a kilometre [recalled]. Where the moved field has a strong
   gradient (a temperature fall of 3 K or more and a wind veer of 45 degrees or more across 20 km [judgement])
   the transition is narrowed to about a kilometre about its middle, keeping its position and speed.
3. **Timing from the stations.** When an airport reports the shift before or after the model has it, the
   front's position is moved to match, and the correction fades with distance as the residuals do (6.3).
4. **On the client.** Today the mean wind is the same everywhere on a map (`WindWeather.At` has no
   position). A front needs it to differ: the broadcast gains the front as a line (a point on it, its
   normal, its speed, its width) and the air behind it, and `WindField` blends the two means by each
   place's signed distance to the moving line. The trees upwind then hear the front first, a few seconds
   apart across a park, from the same field everything else reads. A frame carries at most one front at a
   time; the next arrives when the first has crossed.

A storm's own gust front (its outflow, ahead of the rain) is the cell's, made by the simulation; real data
has nothing that resolves it.

## 8. The server

### 8.1 Layers

```
    Open-Meteo (field, hourly)   METAR (stations, 10 min)   [GLM flashes]   [NWS, US thunder]
                 \                      |                       /               /
               the conditions service (server only): lattice, heights, residuals, series
                                        |
              conditions at a frame and a moment (mean wind, turbulence, T, dew point, p,
              cloud, sun, rain rate and kind and drop sizes, convection, front)
                                        |
              the simulation under the data: cells, front line, hail ceiling, history spin-up
                                        |
              what is held: an admin's /weather, a map's held weather
                                        |
              the per-world weather the host feeds (one per frame or map)
                                        |
              WorldStateUpdate once a second  →  clients: WindField, rain, run-off, thunder
```

The seam is one call made by the host each tick or second: it hands the per-world weather the conditions for
that world at that moment. Whether those came from real data, from the simulation or from a held value, the
per-world weather does not know or care. On a made-up map the host feeds it from the existing fronts
(`WorldEnvironmentSystem`), which become one more conditions source.

### 8.2 Fetching and caching

- **Open-Meteo:** every node in use (the up to 5 by 5 round a frame with a player in it, and the next ring
  of a moving one) is refreshed once an hour, each request the next 48 hours with about 20 variables, many
  nodes to a request. Weight per node: 2 days of 14 times 20 variables of 10 is 0.29, taken as one call a
  node [assumed]. 10,000 calls a day is about 400 nodes refreshed hourly, or 16 frames far apart from each
  other. Magnolia and Albany together are two frames. Nodes nobody has been near for a day stop
  being refreshed.
- **History:** the first time a node is used, its last days (`past_days` 7 for the ground and the 10-hour
  fuel, 42 for the 1,000-hour fuel, a few calls each), once.
- **METAR:** `metars.cache.csv.gz` every ten minutes (144 requests a day, about 40 MB a day), one request for
  the world. Only stations within 3L of a frame are read.
- **On disk** under the world store (`world/weather/`): each node's last series and when it was fetched,
  the METAR file's parse, written whole as tiles are. A restart reads them and fetches nothing that is
  still fresh.
- **Etiquette:** `HttpClient` with a timeout as the other fetchers have, one fetch at a time, backing off
  on errors (a minute, doubling to an hour), and never more than the limits above. A User-Agent that names
  the project and no person (decision 3).

### 8.3 When a fetch fails or the server is offline

- The series fetched last hour already covers the next 48 hours. A failed fetch changes nothing anyone
  hears: the server reads further along what it has, and tries again later.
- Observations older than 90 minutes fade out (6.3); the model alone carries on.
- When the series a node holds is older than six hours [judgement], the frame hands over to the simulated
  weather: the simulation is started from the real conditions at that moment (its targets set to them), and
  from there it rolls on its own with its usual rates. When data returns, the frame eases back to it over
  ten minutes, the same way a front arrives. Neither direction ever steps.
- `/weather` says which it is: "Real weather at Magnolia, as of 18:15", or "Simulated: no weather data
  since 12:00".

### 8.4 The admin override

`/weather` keeps every command it has. On the world, what is held sits above the real conditions:

- `/weather storm`, `/weather rain heavy`, `/weather wind 20 north gusty` hold that value over the
  real one, for the frame the admin is in, arriving at the hand-set rate (five times a front's).
- Cody's 2026-10-08 ask holds here too: each value is held on its own (temperature, cloud, wind speed,
  direction and gusts, precipitation kind, rate and size), so "overcast and windy, no rain" is two held
  values, and the rest stays real.
- `/weather real` lets go of what is held and eases back to the real conditions; `/weather auto` on a
  made-up map stays as it is (the fronts).
- `/weather replay 2025-05-28 14:00` (optional, stage 5) plays a past day's real weather at this place from
  the archive: a real storm, repeatable, for testing.
- `OPENFPS_WEATHER` still pins the whole server for listening tests, and a setting turns real weather off
  for the lab and tests. **Tests never fetch:** the conditions sources take a fake, with recorded sample
  files (small, CC BY or public domain, with their attribution).

### 8.5 Maps that are not real places

- The city, the speedway and every player-built map have no location: they keep the simulated fronts, or
  their held weather, as now.
- A map may name a place to take its weather from (a map setting: "weather of Albany, Oregon" or a latitude
  and longitude). That chooses a source of conditions; it is not a per-map constant in any law. Decision 4.
- A player map placed in the world (streaming stage 4) has the world's weather where it is placed.

### 8.6 The clock

The world's weather is real now, so the world's sky must be at the real hour there. Today the game clock
runs 60 times real time from 08:00, and the temperature curve follows it. Real weather at 18:00 UTC is a
hot Texas afternoon; heard at the game's 03:00 it is nonsense, and the sun that dries the roads would be at
the wrong height. On the world, the game hour becomes the place's real local time (from its longitude, or
the time zone the NWS points answer gives) and the day of the year the real date; made-up maps keep the
game clock. `WindField.Now()` is already UTC. Decision 2.

### 8.7 Privacy

- No player's position, name or address is ever sent anywhere. The only coordinates in a request are
  lattice nodes, on a 0.1 degree grid by construction: the block of nodes asked for places a frame to
  within about 10 km, and a frame holds players up to 6 km from its corner, never one point.
- The METAR file is the whole world in one request, so observations reveal nothing at all.
- Nodes for the world's listed places (Magnolia, Albany) are refreshed whether or not anyone is there, so
  their requests do not say when someone arrives. Nodes elsewhere are fetched when a frame opens there,
  which does say that someone is within about 15 km of that block; decision 3 covers whether that is
  acceptable.
- The User-Agent names the project, not who runs the server, as the existing fetchers do (`Geocoder`,
  `Usgs3Dep`: "openfps-world/0.1").

### 8.8 Attribution

Open-Meteo's CC BY 4.0 asks for "a link next to any location Open-Meteo data are displayed". The game shows
weather in words: `/weather` on the world ends with "Weather data by Open-Meteo.com", and the manual,
readme and the credits give the link. METAR data is the NWS's and needs nothing, but must not be presented
as official, and the NWS's name must not suggest endorsement. The world store's weather files carry their
source and licence, as the tiles carry theirs.

### 8.9 The wire

- On the world, `WorldStateUpdate` is per frame already. The values it carries are the frame's at its
  centre, plus the front (7.4) when one is crossing.
- New fields are appended (positional, append only): the front's line and the air behind it; cloud and
  sun only if a client reads them. A client and a server from either side of the change refuse each other at
  login, as with every wire change; they ship together.
- Nothing about the source goes to clients: they hear weather, not where it came from. `/weather`'s words
  come from the server.

## 9. Cody's rules, and how this keeps them

| Rule | How |
|---|---|
| Sound falls out of physics, materials and geometry, no special cases, no per-map constants | Real fields set conditions only. Rain on a tin roof is the drops' spectrum on the roof's material; wind in the oaks is the field on the trees. No real field selects a sound; no place has its own factor. Corrections (heights, stations, fronts) are the same laws everywhere. |
| No recorded ambience beds | Nothing here plays a recording. A rainy hour in Albany is heard only through the things the rain lands on and the water it makes run. |
| The weather acts on things | The wind field moves foliage, flames and (MATTER step 6) loose things; the rain lands on surfaces, runs off, wets the roads (grip and hiss) and the fuel; the history spin-up makes the ground and fuel as wet as the real days made them; lightning cells can strike and (fire step 3) ignite. |
| Real data is a source of conditions, not a replacement for the simulation | The gusts, cells, fronts, hail and wetness are simulated; real data constrains their averages and timing. Without data the simulation runs alone from the last real state. |
| Every value holdable by hand (CODY_ASKS 12) | Held values sit above the real ones, one at a time (8.4). |

## 10. Stages and effort

A session is one working session of an agent, as in docs/GEOMETRY.md. Each stage leaves the game working
and is heard before the next.

| Stage | Work | Sessions |
|---|---|---|
| 1. Conditions from Open-Meteo for the places | The conditions record and source interface; the Open-Meteo fetcher with the on-disk series; the monotone interpolation in time; mean wind, gust factor to turbulence, temperature, dew point, pressure, rain rate and kind and drop sizes into the per-world weather; the real local clock on the world; the hand-over to the simulation and back; `/weather` says the source; attribution; held values above real ones; tests with recorded samples | 2 |
| 2. Space | The 0.1 degree lattice round each frame, bicubic interpolation, heights from the tiles, travel per frame (and the rebase rule) | 1 to 2 |
| 3. Observations | The METAR file every ten minutes, residuals, Barnes correction with fades and checks; present weather placing and clearing rain | 1 to 2 |
| 4. Cells and fronts | Rain cells conserving the hour's mean; lightning cells from the convective state instead of `WeatherType`; the hail ceiling from CAPE; motion between hours, sharpening, station timing; the front on the wire and in `WindField` | 2 to 3 |
| 5. History and new slots | The spin-up through `RoadWater`, `GroundWater` and fuel moisture; cloud, sun and latitude into Penman; dew; `/weather replay` from the archive | 1 to 2 |
| 6. Optional | GLM flashes over the Americas placed as real strikes; NWS thunder probability in the US | 1 to 2 |

Total: about 8 to 13 sessions, of which stages 1 to 3 (4 to 6) give real weather that is right at the
places, and 4 and 5 make it move and soak in as it really does.

## 11. Risks

- **Open-Meteo's terms.** The free tier is non-commercial; if the game ever has ads or subscriptions it
  needs a paid plan (no prices are published on the pricing page; a key from Stripe checkout) or a
  self-hosted Open-Meteo server (AGPL, and the model downloads it needs). Terms and limits change; the
  server must keep working when it is refused (8.3), and the source is one class behind an interface.
- **Being blocked.** All three services block abusers without notice. The budgets above are a small share of
  the limits, and the fetchers back off.
- **The model's wind.** Models smooth the wind; in the one sample above the model's was a third to a half
  under the airports'. Stage 3 corrects near airports; far from them the wind may be light. Measure against
  several stations over days before trusting the levels, since the trees' sound goes as the wind to about
  the 3.6 (`WindField` comment): a third of the wind is a much quieter wood.
- **The gust mapping.** `TurbulenceFor` clamps the intensity to 0.18 to 0.30; real gusty days over towns
  are 0.3 to 0.4 (the doc comment on `WindField` says so). The clamp may need widening once real gust
  factors arrive; that changes what the trees sound like in a gale and has to be heard.
- **Rain rates above 60 mm/h.** The intensity curve tops out there; anything reading the intensity instead
  of the rate would cap a cloudburst. Every reader should take the rate.
- **The clock.** Real local time on the world means night when Cody plays at night; a tester wanting a
  daytime storm uses held values or `/weather replay`. Two clocks (world and maps) is a new idea in the
  code.
- **Fronts are the hard part.** Motion estimation on a 4 by 4 lattice is coarse; the first version may
  have to use more nodes (6 by 6) and a regional field. Stage 4 is the one most likely to run over.
- **The wire.** Fronts in `WindField` change a pure function many voices read on the mixer thread; it must
  stay allocation-free and NaN-proof (memory: mixer callbacks must not throw).
- **Tests and the internet.** Tests must never fetch (8.4); GitHub runners would be rate-limited and flaky.
- **Privacy.** Nodes fetched on arrival tell Open-Meteo, from the VPS's address, that someone is within
  about 15 km of them (8.7).

## 12. Decisions for Cody

1. **Open-Meteo's free tier, for a game with no ads or subscriptions.** Recommended: yes, with the
   attribution in `/weather` and the credits, and the source behind an interface so a paid key or a
   self-hosted server can replace it. If the game is ever sold or carries ads, it needs a paid plan.
2. **The world's clock.** Recommended: on the world, the real local time and date at the place; made-up maps
   keep the game clock (60 times real time). Real weather at the wrong hour is not real weather.
3. **The User-Agent and on-demand fetching.** Recommended: a User-Agent naming the project and its GitHub
   page, no person and no server owner; the listed places refreshed always, other nodes fetched when a frame
   opens there (which tells Open-Meteo a neighbourhood about 15 km across, never a player).
4. **Made-up maps.** Recommended: the city, the speedway and player maps keep simulated weather; a map
   setting may name a real place to follow (useful for the city), off by default.
5. **Real lightning (GLM) over the Americas.** Recommended: later (stage 6) and only if the simulated cells
   from real convection are not enough by ear; it costs about 1.3 GB a day of downloads while a storm
   is near.
6. **The NWS API.** Recommended: not at first. Open-Meteo and METAR cover what it gives, worldwide; NWS
   thunder probability is a later refinement for the US.
7. **When the data stops.** Recommended: keep using the fetched forecast for up to six hours, then hand over
   to the simulation from the last real state; `/weather` says which.

### Decided (Cody, 2026-10-11)

- **The world's clock: real local time at real places** ("Yes, real local time at real places for the weather.
  Must be realistic to the area as it is happening"). On the world, the time, date and weather at a place are
  what they are there now; made-up maps keep their own clock. The other six decisions are open.
- **Where the sun and the moon are, for the player** (asked the same day). Both are computed from the place's
  latitude and longitude and the real time: the sun by NOAA's solar position equations, the moon by Meeus's
  lunar series (position, phase, illuminated share), each to about a tenth of a degree, more than the ear needs.
  A readout key and `/sky` say each body's bearing and height in the words the tracker already uses ("the sun
  west-southwest, twenty degrees up, sets in an hour and ten minutes; the moon waxing gibbous, three quarters
  lit, southeast, thirty-five degrees up, rises at ..."), below the horizon as "down, rises at ...". Not a sound
  in the sky: the sun is not a source. What the sun's height changes is heard where it is physical: the
  temperature and the wind's daily turn, the dawn chorus and dusk, insects by temperature. It goes with the
  clock in stage 1. The calculations and the sentence are built (`OpenFPS.Astronomy`, docs/SKY.md); the key
  and `/sky` are not.

## 13. Sources

Checked 2026-10-10, by reading the page or by asking the service.

- NWS API documentation (User-Agent, no key, rate limit not public, cache the grid, 2.5 km grid, obs lag up
  to 20 minutes): https://www.weather.gov/documentation/services-web-api
- NWS disclaimer (public domain, no guarantee of delivery, name and logo): https://www.weather.gov/disclaimer
- NWS answers used: https://api.weather.gov/points/30.2094,-95.7508 (HGX 48,114, `max-age` 71,123 s),
  https://api.weather.gov/gridpoints/HGX/52,109 (the gridpoint field list, `max-age` 3,499 s),
  https://api.weather.gov/stations/KCXO/observations/latest
- Open-Meteo forecast API (variables, 15-minutely only in Central Europe and North America, model update
  intervals, stitching, best match, elevation downscaling, `past_days` 0 to 92, preceding-hour sums and
  maxima, WMO codes): https://open-meteo.com/en/docs
- Open-Meteo terms (non-commercial free tier, 600/5,000/10,000 limits, CC BY 4.0, blocking):
  https://open-meteo.com/en/terms
- Open-Meteo licence (attribution wording, underlying sources' licences): https://open-meteo.com/en/licence
- Open-Meteo pricing (300,000 a month, call weighting, the tier table: the Historical Weather API row marked
  for Free, Professional and Enterprise and not Standard, "no uptime guarantee"):
  https://open-meteo.com/en/pricing
- Open-Meteo on multi-location weighting (weight = locations × days/14 × variables/10):
  https://openmeteo.substack.com/p/weather-data-for-multiple-locations
- Open-Meteo historical API (ERA5, ERA5-Land, IFS, CERRA; resolutions and delays):
  https://open-meteo.com/en/docs/historical-weather-api
- Open-Meteo answers used: `api.open-meteo.com/v1/forecast` for 30.2094,-95.7508 and 44.6365,-123.1059
  (current, minutely_15 with `lightning_potential` empty, hourly 700 hPa wind, boundary layer, radiation,
  soil moisture, `past_days` 92 and 93 accepted).
- aviationweather.gov data API (100 requests a minute, 400 entries, cache files every minute, custom
  User-Agent, worldwide METAR): https://aviationweather.gov/data/api/
- aviationweather.gov answers used: https://aviationweather.gov/api/data/metar?ids=KCXO,KDWH,KCVO,KSLE&format=json&hours=2,
  the `bbox=29.5,-96.5,31,-95` query (17 stations round Magnolia), and
  https://aviationweather.gov/data/cache/metars.cache.csv.gz (5,121 stations, 283,638 bytes, `max-age` 30).
- MADIS data restrictions: https://madis.ncep.noaa.gov/madis_restrictions.shtml
- MADIS data application: https://madis.ncep.noaa.gov/data_application.shtml
- Synoptic Data open access (.edu only, research): https://synopticdata.com/open-access
- GOES GLM Level 2 (CC0, 20-second files, about 10 km):
  https://catalog.data.gov/dataset/noaa-goes-r-series-geostationary-lightning-mapper-glm-level-2-lightning-detection-events-g;
  the listing https://noaa-goes19.s3.amazonaws.com/?list-type=2&prefix=GLM-L2-LCFA/2026/283/18/ (55 files
  in the hour so far, about 310 KB each).

## 14. Not verified

- Whether Open-Meteo counts a small request (under 14 days and 10 variables a location) as less than one
  call; the budget assumes one call a location.
- The terms of foreign METARs passed through aviationweather.gov: the NWS's public-domain statement covers
  NWS information; reports from other countries' services reach it by WMO exchange, and their own terms
  were not checked. They are used here only as corrections inside derived values, never republished.
- How far back aviationweather.gov's METAR API answers (`hours`), and the exact METAR coding rules for VRB
  and gusts (FMH-1), the ASOS two-minute against WMO ten-minute mean wind.
- The [recalled] figures: Davenport's peak factor, the lapse rates, Barnes 1964, Fritsch and Carlson, the
  front width, the hail and updraft relation, model grid spacings, roughness lengths.
- The NWS rate limit (not published) and whether its planned API key will be free.
- Open-Meteo's lightning potential: empty in the US on the day asked; where it is filled was not checked.
