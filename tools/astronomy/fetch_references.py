#!/usr/bin/env python3
"""Fetches the published sun and moon values AstronomyTests checks against (docs/SKY.md).

    python3 -I tools/astronomy/fetch_references.py WORK_DIR

Writes OpenFPS.Tests/Astronomy/References/. Every file names the URL each value came from:
  - the US Naval Observatory's Astronomical Applications API (rise, set, transit, phase, fraction,
    celestial-navigation altitude and azimuth) and its year tables (rise and set, three twilights);
  - NOAA's solar calculator: its own calculation functions (gml.noaa.gov/grad/solcalc/main.js), run by
    tools/astronomy/noaa_solcalc.js in a bare node context, as its page runs them.
WORK_DIR holds the downloaded main.js; it is never run from there, only read.
"""
import html, json, os, re, subprocess, sys, time, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "OpenFPS.Tests", "Astronomy", "References")
PLACES = {"magnolia": (30.21, -95.75, -6), "albany": (44.64, -123.10, -8), "tromso": (69.65, 18.96, 1)}
DATES = ["2026-03-20", "2026-06-21", "2026-09-23", "2026-12-21", "2026-06-29", "2026-10-10", "2026-10-11", "2026-10-26"]
HOURS = ["03:00", "09:00", "15:00", "21:00"]


def get(url):
    for _ in range(6):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "open-fps astronomy tests"})
            with urllib.request.urlopen(req, timeout=40) as r:
                time.sleep(0.5)
                return r.read().decode()
        except Exception as e:
            print("retry", url, e, file=sys.stderr)
            time.sleep(10)
    raise SystemExit("failed " + url)


def oneday(f):
    f.write("# USNO Complete Sun and Moon Data for One Day; fraction is at local noon (USNO's note). Times local.\n")
    f.write("place\tlat\tlon\ttz\tdate\tcurphase\tfracillum\tsun\tmoon\turl\n")
    for name, (lat, lon, tz) in PLACES.items():
        for d in DATES:
            url = f"https://aa.usno.navy.mil/api/rstt/oneday?date={d}&coords={lat},{lon}&tz={tz}&dst=false"
            data = json.loads(get(url))["properties"]["data"]
            ev = lambda key: ";".join(f"{e['phen']}={e['time']}" for e in data.get(key, []))
            f.write(f"{name}\t{lat}\t{lon}\t{tz}\t{d}\t{data['curphase']}\t{data['fracillum']}\t{ev('sundata')}\t{ev('moondata')}\t{url}\n")


def celnav(f):
    f.write("# USNO Celestial Navigation Data: hc and zn are geocentric and airless; pa is the parallax in altitude,\n")
    f.write("# refr the refraction (negative, as a sextant correction). Bodies below the horizon have no pa.\n")
    f.write("place\tlat\tlon\tutc\tbody\thc\tzn\tpa\trefr\turl\n")
    rows = []
    for name, (lat, lon, _) in PLACES.items():
        for d in DATES:
            for hh in HOURS:
                url = f"https://aa.usno.navy.mil/api/celnav?date={d}&time={hh}&coords={lat},{lon}"
                for o in json.loads(get(url))["properties"]["data"]:
                    if o["object"] not in ("Sun", "Moon"):
                        continue
                    a, c = o["almanac_data"], o["altitude_corrections"]
                    f.write(f"{name}\t{lat}\t{lon}\t{d}T{hh}\t{o['object']}\t{a['hc']}\t{a['zn']}\t{c['pa']}\t{c['refr']}\t{url}\n")
                    rows.append((name, lat, lon, d, hh, o["object"]))
    return rows


def phases(f):
    url = "https://aa.usno.navy.mil/api/moon/phases/year?year=2026"
    f.write(f"# USNO Phases of the Moon, times UT. {url}\nutc\tphase\n")
    for p in json.loads(get(url))["phasedata"]:
        f.write(f"{p['year']:04d}-{p['month']:02d}-{p['day']:02d}T{p['time']}\t{p['phase']}\n")


def year_tables():
    # Tasks: 0 sun, 1 moon, 2 civil, 3 nautical, 4 astronomical twilight.
    for name, (lat, lon, tz) in PLACES.items():
        for task, kind in enumerate(["sun", "moon", "civil", "nautical", "astronomical"]):
            url = (f"https://aa.usno.navy.mil/calculated/rstt/year?ID=AA&year=2026&task={task}&lat={lat}&lon={lon}"
                   f"&label={name}&tz={abs(tz)}&tz_sign={1 if tz >= 0 else -1}&submit=Get+Data")
            page = get(url)
            text = html.unescape(re.sub(r"<[^>]+>", "", re.search(r"<pre[^>]*>(.*?)</pre>", page, re.S).group(1)))
            lines = [ln.rstrip() for ln in text.splitlines()]
            start = next(i for i, ln in enumerate(lines) if ln.startswith("Location:"))
            body = "\n".join(ln for ln in lines[start:] if ln.strip())
            with open(os.path.join(OUT, f"usno-year-{name}-{kind}.txt"), "w") as f:
                f.write(f"# {url}\n{body}\n")


def noaa(work, rows):
    url = "https://gml.noaa.gov/grad/solcalc/main.js"
    os.makedirs(work, exist_ok=True)
    main_js = os.path.join(work, "main.js")
    with open(main_js, "w") as f:
        f.write(get(url))
    instants = "".join(f"{n} {lat} {lon} {d} {hh}\n" for n, lat, lon, d, hh, body in rows if body == "Sun")
    out = subprocess.run(["node", os.path.join(HERE, "noaa_solcalc.js"), main_js], input=instants,
                         capture_output=True, text=True, check=True).stdout
    with open(os.path.join(OUT, "noaa-sun.tsv"), "w") as f:
        f.write(f"# NOAA solar calculator (https://gml.noaa.gov/grad/solcalc/), its functions from {url}\n")
        f.write("# run by tools/astronomy/noaa_solcalc.js. Azimuth and elevation (refracted), degrees; noon, minutes UTC.\n")
        f.write("place\tlat\tlon\tutc\tazimuth\televation\tnoon\n")
        for ln in out.strip().splitlines():
            n, lat, lon, d, hh, az, el, noon = ln.split()[:8]
            f.write(f"{n}\t{lat}\t{lon}\t{d}T{hh}\t{az}\t{el}\t{noon}\n")


def main():
    work = sys.argv[1]
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "usno-oneday.tsv"), "w") as f:
        oneday(f)
    with open(os.path.join(OUT, "usno-celnav.tsv"), "w") as f:
        rows = celnav(f)
    with open(os.path.join(OUT, "usno-phases-2026.tsv"), "w") as f:
        phases(f)
    year_tables()
    noaa(work, rows)


if __name__ == "__main__":
    main()
