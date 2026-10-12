#!/usr/bin/env python3
"""Sorts the survey's types for the physics boundary (docs/PHYSICS_BOUNDARY.md) and writes its tables.

Input: the TSVs tools/sound_boundary's Roslyn reader writes (types, edges, members, statics, static_refs).
Output: <out>/physics_tables.md (every table the document quotes) and <out>/physics_crossings.tsv (every
reference from a type bound for OpenFPS.Physics to a type it may not reference: anything but Geometry and
Physics itself).

Groups (the first rule that matches a type):
  P   moves to OpenFPS.Physics whole
  PS  splits: the mechanics go to OpenFPS.Physics, the rest stays where it is
  V   a value both sides need: lives in Physics, the host sends it and Sound reads it
  G   stays in OpenFPS.Geometry; Physics calls it
  S   stays in Sound (or Acoustics) and reads physical state
  H   stays in the host (server, client, Common's entity model): steps Physics or adapts the world to it
Types no rule names are outside the inventory and are sorted by project only (host, sound, geometry, ...).
"""
import collections
import csv
import os
import sys

src = sys.argv[1] if len(sys.argv) > 1 else '/tmp/openfps-physurvey/out'
out = sys.argv[2] if len(sys.argv) > 2 else src


def rd(name):
    with open(os.path.join(src, name), encoding='utf-8') as f:
        return list(csv.DictReader(f, delimiter='\t', quoting=csv.QUOTE_NONE))


TYPES = {r['type']: r for r in rd('types.tsv')}
EDGES = rd('edges.tsv')
STATICS = rd('statics.tsv')
SREFS = rd('static_refs.tsv')

C = 'OpenFPS.Common.'
G = 'OpenFPS.Common.Geometry.'
EN = 'OpenFPS.Client.AudioEngine.Core.Engine.'

# ── The inventory: (type, group, note) ─────────────────────────────────────────────────────────────
RULES = [
    # Common: player movement and the ground probes
    (C + 'SharedMovementEngine', 'P', 'one movement step, client and server'),
    (C + 'PhysicsUtils', 'PS', 'ground probes: the triangle-world parts move; the World/WorldSnapshot overloads are host adapters'),
    (C + 'PhysicsConstants', 'PS', 'body and gravity to Physics; tick rate and input integrity stay with the network'),
    (C + 'GroundProbeMemo', 'H', 'the server\'s per-session cache of the ground probe'),
    # Sound: the body constants, wheels, tyres, chassis, presets
    (C + 'BodyConstants', 'P', 'person height, walk, run, gravity'),
    (C + 'ChassisSpec', 'P', 'axles, centre of gravity, roll stiffness, steering lock'),
    (C + 'AxleSpec', 'P', ''),
    (C + 'TyreSize', 'P', ''),
    (C + 'BrakeKind', 'V', 'mechanics, and the brake squeal reads it'),
    (C + 'RoadSurfaces', 'V', 'surface grip (Physics) and the surface index the tyres sound by'),
    (C + 'RoadGripLaw', 'P', 'a tyre on a wet road: the film, aquaplaning, the grip left'),
    (C + 'RoadTexture', 'V', 'a surface as the wet grip and the water read it'),
    (C + 'MagicCurve', 'P', 'the Magic Formula'),
    (C + 'WheelDynamics', 'P', 'planar per-wheel vehicle dynamics'),
    (C + 'TyreGrip', 'P', 'the friction circle: the demand on a tyre against what it can give'),
    (C + 'TyreMechanics', 'P', 'the physics half of a tyre preset: grip, Magic Formula shape, pressure, tread depth'),
    (C + 'VehicleMechanics', 'P', 'the physics half of a vehicle preset: running gear, tyres, mass'),
    (C + 'TyreFriction', 'S', 'the squeal onsets and amounts (the friction circle went to TyreGrip, physics stage 2)'),
    (C + 'RunningGear', 'P', 'every preset\'s running gear as data'),
    (C + 'Networking.WheelState', 'V', 'one wheel on the wire'),
    (C + 'Gearbox', 'P', 'ratios, final drive, shift points'),
    (C + 'TyreProfile', 'S', 'tread and cavity (its mechanics are TyreMechanics, physics stage 2)'),
    (C + 'VehicleProfile', 'PS', 'a preset: mass, drag, rolling resistance, running gear are mechanics; the rest is sound'),
    (C + 'EngineMechanics', 'P', 'the physics half of an engine preset: idle, redline, peak torque and its rpm'),
    (C + 'EngineProfile', 'PS', 'a preset: torque, inertia, friction, idle and redline are mechanics; the gas path is sound'),
    (C + 'VehicleSpec', 'PS', 'the editable vehicle kind: chassis to Physics, exhaust and intake to Sound'),
    (C + 'VehicleChassisSpec', 'P', ''),
    (C + 'VehicleExhaustSpec', 'S', ''),
    (C + 'VehicleIntakeSpec', 'S', ''),
    (C + 'VehicleBody', 'S', 'panel modes and the cabin air: how the engine gets out, not how the car moves'),
    (C + 'BodyResonator', 'S', ''),
    (C + 'BodyMode', 'S', ''),
    (C + 'VehicleCabin', 'S', 'the shell as boxes; geometry stage 6 makes it a mesh both sides read'),
    (C + 'DoorPhysics', 'S', 'audio-rate structural dynamics of door parts (modes, plates, LuGre)'),
    (EN + 'Driveline', 'PS', 'clutch, gears, mass and resistances at the audio rate: the mechanics belong in Physics at the tick'),
    (EN + 'Driver', 'S', 'a scripted driver for the lab and the bench'),
    (EN + 'VirtualDriver', 'S', 'the sound\'s guess at the driver from the server\'s speed'),
    (EN + 'DriveOrder', 'S', ''),
    (EN + 'DriverAction', 'S', ''),
    # Sound: projectiles and air
    (C + 'ExternalBallistics', 'P', 'point-mass flight with drag'),
    (C + 'BulletState', 'V', ''),
    (C + 'FlightPoint', 'P', ''),
    (C + 'Projectile', 'V', 'what flies, made by a weapon'),
    (C + 'Air', 'V', 'temperature and pressure: density for drag, and the sound\'s air'),
    (C + 'Ballistics', 'S', 'the crack and the muzzle blast'),
    (C + 'Ricochet', 'PS', 'the bounce and the tumbling flight are mechanics; the whine is sound'),
    (C + 'WindField', 'P', 'the moving air: log law, eddies, carry'),
    (C + 'WindAir', 'V', 'the wind the server sends'),
    (C + 'WindWeather', 'V', 'one weather\'s wind ramp'),
    (C + 'WindModel', 'P', 'the bullets\' second wind law; to be one law with WindField'),
    (C + 'WorldWeather', 'S', 'one audio world\'s weather: it holds the wind Physics owns'),
    (C + 'EarWind', 'S', 'the wind at the ears: reads the air'),
    (C + 'ImpactAcoustics', 'S', 'the sound of an impact from two masses and a closing speed'),
    # Geometry
    (G + 'SolidContact', 'G', 'cylinder and capsule against a solid'),
    (G + 'BodyShape', 'G', ''),
    (G + 'WheelRays', 'G', 'a ray down at each wheel; Rest (the pose from the rays) is mechanics'),
    (G + 'WheelContact', 'G', ''),
    (G + 'TriangleWorld', 'G', 'the BVH and its queries'),
    (G + 'BvhBuilder', 'G', ''),
    (G + 'Heightfield', 'G', ''),
    (C + 'GeometryUtils', 'G', 'boxes against cylinders, OBB tests'),
    (C + 'SpatialGrid', 'G', 'the entity grid'),
    # Server
    (C.replace('Common', 'Server') + 'Systems.MovementSystem', 'H', 'steps players from their inputs'),
    ('OpenFPS.Server.Systems.DrivingSystem', 'PS', 'steps a driven car; its engine, resistances and collision are mechanics'),
    ('OpenFPS.Server.Systems.VehicleSystem', 'H', 'traffic on its lines, kinematic'),
    ('OpenFPS.Server.Systems.RailSystem', 'H', 'trains on their tracks, kinematic'),
    ('OpenFPS.Server.Systems.OccupancySystem', 'H', 'seats a body on its vehicle'),
    ('OpenFPS.Server.Core.OccupancyService', 'H', ''),
    ('OpenFPS.Server.Systems.DoorSystem', 'H', 'door leaves on a timer'),
    ('OpenFPS.Server.Core.CombatService', 'H', 'bullets stepped through ExternalBallistics; BodyFall'),
    ('OpenFPS.Server.Core.Bodies', 'H', 'a body left where somebody fell'),
    ('OpenFPS.Server.Core.HandsService', 'H', 'drops land at sqrt(2gh)'),
    (C + 'LineFollower', 'H', 'a driver for traffic'),
    (C + 'RaceLine', 'H', 'speeds along a line from curvature and grip'),
    # Client
    ('OpenFPS.Client.Core.ClientPhysicsSystem', 'H', 'the client\'s prediction of its own body'),
    ('OpenFPS.Client.Core.OtherBodies', 'H', 'other people\'s feet from their transforms'),
]
RULE = {t: (g, n) for t, g, n in RULES}
missing = [t for t in RULE if t not in TYPES]
if missing:
    print('not in types.tsv: ' + ', '.join(missing), file=sys.stderr)

SKIP_PROJECTS = {'OpenFPS.Tests', 'OpenFPS.AudioLab'}


def project_class(t):
    """Where a type outside the inventory sits, by project and namespace."""
    r = TYPES.get(t)
    if r is None:
        return '?'
    p = r['project']
    if p == 'OpenFPS.Geometry':
        return 'geometry'
    if p in ('OpenFPS.Sound', 'OpenFPS.Acoustics', 'OpenFPS.Native'):
        return 'sound' if p != 'OpenFPS.Acoustics' else 'acoustics'
    if p in SKIP_PROJECTS:
        return 'lab/tests'
    return 'host'


def group(t):
    if t in RULE:
        return RULE[t][0]
    return None


def to_physics(t):
    return group(t) in ('P', 'V')


def allowed_for_physics(t):
    g = group(t)
    if g in ('P', 'PS', 'V', 'G'):
        return True
    return project_class(t) == 'geometry'


def short(t):
    for p in ('OpenFPS.Common.Geometry.', 'OpenFPS.Common.Networking.', 'OpenFPS.Common.Components.', 'OpenFPS.Common.',
              'OpenFPS.Client.AudioEngine.Core.Engine.', 'OpenFPS.Server.Systems.', 'OpenFPS.Server.Core.',
              'OpenFPS.Client.Core.'):
        if t.startswith(p):
            return t[len(p):]
    return t


lines_md = []


def w(s=''):
    lines_md.append(s)


# ── Callers and host reads per inventory type ─────────────────────────────────────────────────────
inbound = collections.defaultdict(lambda: collections.Counter())      # type -> project -> refs
inbound_files = collections.defaultdict(set)
outbound = collections.defaultdict(lambda: collections.Counter())     # type -> (class, target) -> refs
for e in EDGES:
    if e['from_project'] in SKIP_PROJECTS:
        lab = True
    else:
        lab = False
    ft, tt = e['from_type'], e['to_type']
    if tt in RULE and ft != tt:
        inbound[tt][e['from_project'].replace('OpenFPS.', '')] += 1
        if not lab:
            inbound_files[tt].add(e['file'])
    if ft in RULE and not lab and tt != ft:
        g = group(tt)
        cls = ('inventory:' + g) if g else project_class(tt)
        outbound[ft][(cls, tt)] += 1

w('## Inventory')
w()
w('| Type | Group | File | Lines | Callers (refs by project) | Reads outside Geometry and the inventory |')
w('|---|---|---|---:|---|---|')
for t, g, note in RULES:
    r = TYPES.get(t)
    if r is None:
        continue
    callers = ', '.join(f'{p} {n}' for p, n in inbound[t].most_common())
    reads = collections.Counter()
    for (cls, tt), n in outbound[t].items():
        if cls in ('host', 'sound', 'acoustics'):
            reads[f'{short(tt)} ({cls})'] += n
    rs = ', '.join(f'{k} {n}' for k, n in reads.most_common(6))
    if len(reads) > 6:
        rs += f' ... (+{len(reads) - 6})'
    w(f'| {short(t)} | {g} | {r["file"]}:{r["line"]} | {r["lines"]} | {callers} | {rs} |')
w()

# ── Totals by group ───────────────────────────────────────────────────────────────────────────────
w('## Lines by group')
w()
w('| Group | Types | Lines |')
w('|---|---:|---:|')
tot = collections.defaultdict(lambda: [0, 0])
for t, g, _ in RULES:
    if t in TYPES:
        tot[g][0] += 1
        tot[g][1] += int(TYPES[t]['lines'])
for g in ('P', 'PS', 'V', 'G', 'S', 'H'):
    w(f'| {g} | {tot[g][0]} | {tot[g][1]} |')
w()

# ── Crossings: from what moves to Physics into what it may not reference ──────────────────────────
crossings = []
for e in EDGES:
    if e['from_project'] in SKIP_PROJECTS:
        continue
    ft, tt = e['from_type'], e['to_type']
    if group(ft) in ('P', 'V') and not allowed_for_physics(tt):
        crossings.append(e)
with open(os.path.join(out, 'physics_crossings.tsv'), 'w', encoding='utf-8') as f:
    f.write('from_type\tfile\tline\tto_type\tto_class\tmember\tfrom_member\n')
    for e in sorted(crossings, key=lambda e: (e['file'], int(e['line']))):
        tt = e['to_type']
        cls = ('inventory:' + group(tt)) if group(tt) else project_class(tt)
        f.write('\t'.join([e['from_type'], e['file'], e['line'], tt, cls, e['member'], e['from_member']]) + '\n')

w('## Crossings from the types that move whole (P, V)')
w()
w(f'{len(crossings)} references from {len({e["file"] for e in crossings})} files. Every line: physics_crossings.tsv.')
w()
w('| From | To | Class | Refs | Lines |')
w('|---|---|---|---:|---|')
byft = collections.defaultdict(list)
for e in crossings:
    byft[(e['from_type'], e['to_type'])].append(e)
for (ft, tt), es in sorted(byft.items(), key=lambda kv: -len(kv[1])):
    cls = ('inventory:' + group(tt)) if group(tt) else project_class(tt)
    ls = sorted({int(e['line']) for e in es})
    lstr = ', '.join(map(str, ls[:10])) + (f' ... ({len(ls)} lines)' if len(ls) > 10 else '')
    w(f'| {short(ft)} | {short(tt)} | {cls} | {len(es)} | {es[0]["file"]}: {lstr} |')
w()

# ── The split types: which members the mechanics read and which the sound reads ───────────────────
MECH_READERS = {t for t, g, _ in RULES if g in ('P', 'PS', 'V')} | {
    'OpenFPS.Server.Systems.VehicleSystem', 'OpenFPS.Server.Systems.RailSystem', C + 'LineFollower', C + 'RaceLine',
    'OpenFPS.Server.Core.OccupancyService', 'OpenFPS.Server.Systems.MovementSystem', 'OpenFPS.Server.Core.CombatService',
    'OpenFPS.Server.Core.VehicleShell', 'OpenFPS.Server.Core.CompositeService'}


def side(e):
    ft = e['from_type']
    if e['from_project'] in SKIP_PROJECTS:
        return 'lab'
    if ft in MECH_READERS and group(ft) != 'PS' or ft in ('OpenFPS.Server.Systems.DrivingSystem', EN + 'Driveline', C + 'WheelDynamics'):
        return 'mechanics'
    if e['from_project'] in ('OpenFPS.Sound', 'OpenFPS.Acoustics') or e['file'].startswith('OpenFPS.Client.Core/AudioEngine') \
            or 'ClientAudioSystem' in e['file'] or group(ft) == 'S':
        return 'sound'
    return 'other host'


w('## Members of the split presets by who reads them')
w()
for preset in (C + 'VehicleProfile', C + 'EngineProfile', C + 'TyreProfile', C + 'Gearbox', C + 'ChassisSpec'):
    reads = collections.defaultdict(collections.Counter)
    for e in EDGES:
        if e['to_type'] != preset or e['symbol_kind'] not in ('Property', 'Field', 'Method'):
            continue
        reads[e['member']][side(e)] += 1
    if not reads:
        continue
    mech_only = sorted(m for m, c in reads.items() if c['mechanics'] and not c['sound'])
    sound_only = sorted(m for m, c in reads.items() if c['sound'] and not c['mechanics'])
    both = sorted(m for m, c in reads.items() if c['sound'] and c['mechanics'])
    neither = sorted(m for m, c in reads.items() if not c['sound'] and not c['mechanics'])
    w(f'### {short(preset)}')
    w()
    w(f'- members read: {len(reads)}; by the mechanics only {len(mech_only)}, by the sound only {len(sound_only)}, '
      f'by both {len(both)}, by neither (lab, tests, other host) {len(neither)}')
    w(f'- mechanics only: {", ".join(mech_only)}')
    w(f'- both: {", ".join(both)}')
    if len(sound_only) <= 40:
        w(f'- sound only: {", ".join(sound_only)}')
    w()

# ── Who will use Physics ──────────────────────────────────────────────────────────────────────────
w('## References into what moves to Physics (P, PS, V), by project')
w()
w('| Project | Refs | Files |')
w('|---|---:|---:|')
use = collections.Counter()
usef = collections.defaultdict(set)
for e in EDGES:
    if group(e['to_type']) in ('P', 'PS', 'V') and group(e['from_type']) not in ('P', 'PS', 'V'):
        use[e['from_project']] += 1
        usef[e['from_project']].add(e['file'])
for p, n in use.most_common():
    w(f'| {p} | {n} | {len(usef[p])} |')
w()

# Sound's reads of physical state, by type.
w('### Sound and the client audio reading the mechanics (P, PS, V)')
w()
w('| Mechanics type | Refs from Sound | Refs from Client.Core audio | Files |')
w('|---|---:|---:|---:|')
sr = collections.defaultdict(lambda: [0, 0, set()])
for e in EDGES:
    if group(e['to_type']) not in ('P', 'PS', 'V') or group(e['from_type']) in ('P', 'PS', 'V'):
        continue
    if e['from_project'] == 'OpenFPS.Sound':
        sr[e['to_type']][0] += 1
        sr[e['to_type']][2].add(e['file'])
    elif e['file'].startswith('OpenFPS.Client.Core/AudioEngine') or 'ClientAudioSystem' in e['file']:
        sr[e['to_type']][1] += 1
        sr[e['to_type']][2].add(e['file'])
for t, (a, b, fs) in sorted(sr.items(), key=lambda kv: -(kv[1][0] + kv[1][1])):
    w(f'| {short(t)} | {a} | {b} | {len(fs)} |')
w()

# ── Statics in what moves ─────────────────────────────────────────────────────────────────────────
w('## Statics that can change, in the inventory types bound for Physics (P, PS, V)')
w()
w('| Static | Mutability | Thread-static | Writes outside its type | File:line |')
w('|---|---|---|---:|---|')
writes = collections.Counter()
for r in SREFS:
    owner = r['symbol'].rsplit('.', 1)[0]
    if r['access'] in ('write', 'mutate') and r['from_type'] != owner:
        writes[r['symbol']] += 1
for s in STATICS:
    if group(s['owner']) in ('P', 'PS', 'V') or s['owner'] == 'OpenFPS.Server.Systems.DrivingSystem':
        w(f'| {short(s["symbol"])} | {s["mutability"]} | {s["threadstatic"]} | {writes[s["symbol"]]} | {s["file"]}:{s["line"]} |')
w()

with open(os.path.join(out, 'physics_tables.md'), 'w', encoding='utf-8') as f:
    f.write('\n'.join(lines_md) + '\n')
print(f'tables: {os.path.join(out, "physics_tables.md")}  crossings: {len(crossings)}')
