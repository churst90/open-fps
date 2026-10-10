#!/usr/bin/env python3
"""Sorts the survey's types into the library, the host and the values both need, and writes the
tables of docs/SOUND_LIBRARY_BOUNDARY.md.

Input: the TSVs SoundBoundary writes (types, edges, members, statics, static_refs, assets).
Output: <out>/tables.md (every table the document quotes) and <out>/crossings.tsv (every edge that
crosses the proposed boundary, one row per reference).

The rules are below, in one place: a file's group, then the per-type exceptions. A type's group is
the first rule that matches; a type no rule matches is reported as UNSORTED so a new file cannot slip
through without a decision.

Groups:
  a  belongs in the library, with the project it goes to (Geometry, Acoustics, Sound, Audio, Native)
  b  host-side: session, snapshots, entities, network, UI, game rules, open-fps's adapters
  c  a value both need: it lives in the library (project given) and the host builds or sends it
"""
import collections
import csv
import os
import re
import sys

src = sys.argv[1] if len(sys.argv) > 1 else '/tmp/openfps-wt-survey/out'
out = sys.argv[2] if len(sys.argv) > 2 else src


def rd(name):
    with open(os.path.join(src, name), encoding='utf-8') as f:
        return list(csv.DictReader(f, delimiter='\t', quoting=csv.QUOTE_NONE))


TYPES = {r['type']: r for r in rd('types.tsv')}
EDGES = rd('edges.tsv')
STATICS = rd('statics.tsv')
SREFS = rd('static_refs.tsv')
ASSETS = rd('assets.tsv')
MEMBERS = rd('members.tsv')

CC = 'OpenFPS.Client.Core/'
CO = 'OpenFPS.Common/'

# ── File rules: (path prefix or exact path, group, project, note) ──────────────────────────────────
FILE_RULES = [
    # The library's own projects, once a stage has made them: everything in one is that project's.
    ('OpenFPS.Geometry/', 'a', 'Geometry', ''),
    ('OpenFPS.Acoustics/', 'a', 'Acoustics', ''),
    ('OpenFPS.Sound/', 'a', 'Sound', ''),
    ('OpenFPS.Native/', 'a', 'Native', ''),
    ('OpenFPS.Audio/', 'a', 'Audio', ''),
    # The real-time runtime: FMOD DSPs, voices, Steam Audio, the acoustic worker
    (CC + 'AudioEngine/Data/', 'c', 'Audio', 'the source description the host fills'),
    (CC + 'AudioEngine/Fmod/', 'a', 'Audio', ''),
    (CC + 'AudioEngine/SteamAudio/', 'a', 'Audio', ''),
    (CC + 'AudioEngine/Acoustics/', 'a', 'Audio', ''),
    (CC + 'AudioEngine/Core/AudioEngineFacade.cs', 'a', 'Audio', ''),
    (CC + 'AudioEngine/Core/VoiceManager.cs', 'a', 'Audio', ''),
    (CC + 'AudioEngine/Core/AudioBank.cs', 'a', 'Audio', ''),
    (CC + 'AudioEngine/Core/AmbisonicFormat.cs', 'a', 'Audio', ''),
    (CC + 'AudioEngine/Core/DoorRenderCache.cs', 'a', 'Audio', 'disk cache of door renders'),
    # Synthesis: managed, no FMOD
    (CC + 'AudioEngine/Core/', 'a', 'Sound', 'synthesis'),
    # Integration files: what of them is library and what is open-fps's adapter
    (CC + 'ClientAudioSystem.cs', 'b', '', 'SPLIT: the host-neutral audio world goes to Audio, the snapshot adapter stays'),
    (CC + 'WorldAudioPlayer.cs', 'b', '', 'SPLIT: one-off sounds and echoes go to Audio, WorldAudioEvent decoding stays'),
    (CC + 'SpatialService.cs', 'b', '', 'SPLIT: ray and occlusion queries go to Acoustics over the geometry input; entity lookups stay'),
    (CC + 'ClientGeometry.cs', 'b', '', "open-fps's geometry adapter (entities to TriangleWorld)"),
    (CC + 'BirdLife.cs', 'b', '', 'SPLIT: species choice and calling go to Sound/Audio; the habitat survey reads entities'),
    (CC + 'NearDrops.cs', 'a', 'Audio', ''),
    (CC + 'RainField.cs', 'a', 'Audio', ''),
    (CC + 'ListeningCalibration.cs', 'b', '', 'a UI flow; uses Loudness'),
    (CC + 'AudioDiagnostics.cs', 'b', '', 'a console test'),
    (CC + 'Platform/', 'b', '', ''),
    (CC + 'Session/', 'b', '', ''),
    (CC + 'Input/', 'b', '', ''),
    (CC + 'Services/', 'b', '', ''),
    (CC, 'b', '', 'client game'),
    # Common: acoustics
    (CO + 'AcousticConstants.cs', 'a', 'Acoustics', ''),
    (CO + 'AcousticMap.cs', 'a', 'Acoustics', ''),
    (CO + 'AcousticRegistry.cs', 'a', 'Acoustics', 'materials'),
    (CO + 'Diffraction.cs', 'a', 'Acoustics', ''),
    (CO + 'EarlyReflections.cs', 'a', 'Acoustics', ''),
    (CO + 'Enclosure.cs', 'a', 'Acoustics', ''),
    (CO + 'ImageSource.cs', 'a', 'Acoustics', ''),
    (CO + 'OpeningRoutes.cs', 'a', 'Acoustics', ''),
    (CO + 'PanelAcoustics.cs', 'a', 'Acoustics', ''),
    (CO + 'RoomAcoustics.cs', 'a', 'Acoustics', ''),
    (CO + 'SparseAcousticOctree.cs', 'a', 'Acoustics', ''),
    (CO + 'Systems/', 'a', 'Acoustics', 'regions and openings from the solids'),
    (CO + 'WallTransmission.cs', 'a', 'Acoustics', ''),
    (CO + 'Localisation.cs', 'a', 'Acoustics', ''),
    # Common: sound models, presets, the ear, weather, nature
    (CO + 'Hearing/', 'a', 'Sound', 'the ear'),
    (CO + 'Loudness.cs', 'a', 'Sound', 'the loudness law'),
    (CO + 'Spectrum.cs', 'a', 'Sound', ''),
    (CO + 'ShapedNoise.cs', 'a', 'Sound', ''),
    (CO + 'AudioClock.cs', 'a', 'Sound', 'becomes an instance the host passes in'),
    # Where a source's sound comes out: it reads the vehicle and machine presets, so Sound, not Acoustics. It
    # reads entity snapshots too, so it stays in Common until the world input (stage 6).
    (CO + 'AudioEmission.cs', 'a', 'Sound', 'stays in Common until stage 6: reads entity snapshots'),
    (CO + 'Speech/', 'b', '', "open-fps's recorded lines; Loudness reads its levels"),
    (CO + 'Beacons.cs', 'b', '', 'game feature'),
    (CO + 'DirectionWords.cs', 'b', '', 'speech UI'),
    (CO + 'PlayerCoordinates.cs', 'b', '', 'UI'),
    (CO + 'LaneGuide.cs', 'b', '', 'driving aid'),
    (CO + 'LaneRoutes.cs', 'b', '', 'traffic'),
    (CO + 'LineFollower.cs', 'b', '', 'traffic driver (Resonance keeps its own in Runtime)'),
    (CO + 'RaceLine.cs', 'b', '', 'traffic'),
    (CO + 'Roads.cs', 'b', '', 'map data'),
    (CO + 'Scopes.cs', 'b', '', 'weapon UI'),
    (CO + 'SharedMovementEngine.cs', 'b', '', 'player movement'),
    (CO + 'PhysicsUtils.cs', 'b', '', 'player movement'),
    (CO + 'PhysicsConstants.cs', 'b', '', 'player movement'),
    (CO + 'GroundProbeMemo.cs', 'b', '', 'server movement'),
    (CO + 'EntityGeometry.cs', 'b', '', "open-fps's entities to solids: the adapter the geometry library is fed through"),
    (CO + 'MoverPoses.cs', 'b', '', "the server's count of door leaves moved"),
    (CO + 'DrivingCuePlanner.cs', 'b', '', 'driving aid over the road network'),
    (CO + 'RoadMapData.cs', 'b', '', 'map data (the MapRoads message)'),
    (CO + 'UpdateThrottle.cs', 'b', '', 'client loop'),
    (CO + 'TrackClearance.cs', 'b', '', 'server map validation'),
    (CO + 'Messages.cs', 'b', '', 'network'),
    (CO + 'StatePacking.cs', 'b', '', 'network'),
    (CO + 'DistantMotion.cs', 'b', '', 'network: how a far thing is carried between states'),
    (CO + 'JsonConverters.cs', 'b', '', 'network'),
    (CO + 'Components.cs', 'b', '', 'entity model'),
    (CO + 'WorldSnapshot.cs', 'b', '', 'snapshots'),
    (CO + 'Tiles.cs', 'b', '', 'streaming interest'),
    (CO + 'AudioEvents.cs', 'b', '', 'network: the message carrying TransientSounds'),
    (CO + 'TransientSound.cs', 'c', 'Sound', 'a short sound by its physics; on the wire in WorldAudioEvent'),
    (CO + 'Weapons.cs', 'c', 'Sound', 'weapon numbers the sound reads'),
    (CO + 'ExternalBallistics.cs', 'a', 'Sound', 'bullet flight; the server flies rounds with it too'),
    (CO, 'a', 'Sound', 'models and presets'),
]

# ── Type exceptions: full name -> (group, project, note) ─────────────────────────────────────────
TYPE_RULES = {
    'OpenFPS.Common.Networking.WheelState': ('c', 'Sound', 'a sound value in the network namespace'),
    'OpenFPS.Common.Networking.WorldAudioEvent': ('b', '', 'network message carrying a TransientSound'),
    'OpenFPS.Common.Components.PlaybackMode': ('c', 'Sound', 'an enum the emitter component and the voices share'),
    'OpenFPS.Common.Components.WeatherType': ('c', 'Sound', ''),
    'OpenFPS.Common.Components.ColliderShape': ('c', 'Geometry', ''),
    'OpenFPS.Common.TileKey': ('c', 'Geometry', 'a tile of the world; the tile scenes key on it'),
    'OpenFPS.Common.Precipitation': ('c', 'Sound', 'weather value in the snapshot'),
    'OpenFPS.Common.PrecipitationKind': ('c', 'Sound', ''),
    'OpenFPS.Common.WindAir': ('c', 'Sound', 'the wind the server sends'),
    'OpenFPS.Common.LightningStrike': ('c', 'Sound', 'the flash the server sends'),
    'OpenFPS.Common.FlashKind': ('c', 'Sound', ''),
    'OpenFPS.Common.DoorKind': ('c', 'Sound', 'stored in DoorComponent'),
    'OpenFPS.Common.AdminGunMode': ('c', 'Sound', ''),
    'OpenFPS.Common.CrowdApplause': ('c', 'Sound', ''),
    'OpenFPS.Common.Breath': ('c', 'Sound', ''),
    'OpenFPS.Common.RoadWater': ('c', 'Sound', 'server-advanced store sent whole; the sound reads depths'),
    'OpenFPS.Common.PuddleField': ('c', 'Sound', ''),
    'OpenFPS.Common.WeaponRegistry': ('c', 'Sound', 'content registry both read'),
    'OpenFPS.Common.FireSelector': ('b', '', 'game rule'),
    'OpenFPS.Common.AmmoType': ('b', '', 'game rule'),
    'OpenFPS.Common.Ammunition': ('b', '', 'game rule'),
    'OpenFPS.Common.MathHelper': ('a', 'Geometry', ''),
    'OpenFPS.Client.Core.RainSurvey': ('a', 'Audio', 'reads entities to find roofs and gutters: becomes a reader of the world input'),
    'OpenFPS.Client.Core.DropBank': ('a', 'Audio', ''),
    'OpenFPS.Client.Core.Platform.FmodMicrophoneCapture': ('b', '', 'microphone for voice chat (uses Native)'),
    'OpenFPS.Client.AudioEngine.Fmod.TalkerStream': ('a', 'Audio', 'plays decoded voice-chat PCM; the codec is host-side'),
    'OpenFPS.Client.AudioEngine.Core.ScoredCandidate': ('a', 'Audio', ''),
    'OpenFPS.Client.AudioEngine.Core.IVoiceSink': ('a', 'Audio', ''),
}

# The library's layers, lowest first. A project may use only those before it in this list (Native
# stands apart: only Audio uses it).
LAYERS = {'Geometry': 0, 'Acoustics': 1, 'Sound': 2, 'Native': 0, 'Audio': 3}
ALLOWED = {
    'Geometry': {'Geometry'},
    'Acoustics': {'Geometry', 'Acoustics'},
    'Sound': {'Geometry', 'Acoustics', 'Sound'},
    'Native': {'Native'},
    'Audio': {'Geometry', 'Acoustics', 'Sound', 'Native', 'Audio'},
}


# Projects whose files are sorted by the rules above; every other project is host.
SCANNED = ('OpenFPS.Common', 'OpenFPS.Client.Core', 'OpenFPS.Geometry', 'OpenFPS.Acoustics', 'OpenFPS.Sound',
           'OpenFPS.Native', 'OpenFPS.Audio')


def classify(t):
    if t in TYPE_RULES:
        return TYPE_RULES[t]
    r = TYPES.get(t)
    if not r:
        return ('?', '', '')
    f = r['file']
    if r['project'] not in SCANNED:
        return ('b', '', r['project'])
    for prefix, g, p, note in FILE_RULES:
        if f == prefix or (prefix.endswith('/') and f.startswith(prefix)):
            return (g, p, note)
    return ('UNSORTED', '', '')


GROUP = {t: classify(t) for t in TYPES}
unsorted = [t for t, g in GROUP.items() if g[0] == 'UNSORTED']


def lib(t):
    return GROUP.get(t, ('?',))[0] in ('a', 'c')


def proj(t):
    return GROUP[t][1]


# The universe: every type declared in a sound/acoustics/geometry file or an integration file, and
# every repository type those reference.
SOUND_FILES = re.compile(r'^(OpenFPS\.Client\.Core/(AudioEngine|FmodNative)/|OpenFPS\.Client\.Core/('
                         r'ClientAudioSystem|WorldAudioPlayer|RainField|BirdLife|SpatialService|NearDrops|ClientGeometry'
                         r')\.cs|OpenFPS\.Client\.Core/Platform/(NativeAudioLibraries|BackgroundPriority)\.cs)')
seed = {t for t, r in TYPES.items() if SOUND_FILES.match(r['file'])
        or (r['project'] in SCANNED and r['project'] != 'OpenFPS.Client.Core' and GROUP[t][0] in ('a', 'c'))}
universe = set(seed)
for e in EDGES:
    if e['from_type'] in seed and e['to_type'] in TYPES and TYPES[e['to_type']]['project'] in SCANNED:
        universe.add(e['to_type'])

# ── Crossings ────────────────────────────────────────────────────────────────────────────────────
crossings = []        # library -> host
layering = []         # library -> a higher library layer
for e in EDGES:
    a, b = e['from_type'], e['to_type']
    if a not in GROUP or b not in GROUP:
        continue
    if not lib(a):
        continue
    if GROUP[b][0] == 'b':
        crossings.append(e)
    elif lib(b):
        pa, pb = proj(a), proj(b)
        if pb not in ALLOWED.get(pa, set()):
            layering.append(e)

with open(os.path.join(out, 'crossings.tsv'), 'w', encoding='utf-8') as f:
    f.write('from_type\tfrom_group\tfile\tline\tto_type\tmember\n')
    for e in sorted(crossings, key=lambda e: (e['file'], int(e['line']))):
        f.write(f"{e['from_type']}\t{GROUP[e['from_type']][0]}:{proj(e['from_type'])}\t{e['file']}\t{e['line']}\t{e['to_type']}\t{e['member']}\n")

# The ratchet's allowance (OpenFPS.Tests/LibraryBoundary/allowed.tsv): the crossings per file and host
# type. LibraryBoundaryTests counts the same way and writes the same list (OPENFPS_BOUNDARY_WRITE=all).
allowance = collections.Counter((e['file'], e['to_type']) for e in crossings)
with open(os.path.join(out, 'allowed.tsv'), 'w', encoding='utf-8') as f:
    f.write('file\tname\tcount\n')
    for (path, to), n in sorted(allowance.items()):
        f.write(f'{path}\t{to}\t{n}\n')

# ── Every file of the sorted projects, for the ratchet test (OpenFPS.Tests/LibraryBoundaryTests.cs) ──
# lib: every type in it is library (a or c); host: none is; mixed: both, and the library types are named
# so the test scans only their declarations. A new file in one of these projects has no line here and
# fails the test until it is sorted (a rule above) and this is run again.
by_file = collections.defaultdict(list)
for t, r in TYPES.items():
    if r['project'] in SCANNED:
        for f in r['files'].split(';'):
            by_file[f].append(t)
with open(os.path.join(out, 'files.tsv'), 'w', encoding='utf-8') as f:
    f.write('file\tgroup\tprojects\tlibrary_types\n')
    for path in sorted(by_file):
        ts = by_file[path]
        libs = sorted(t for t in ts if lib(t))
        group = 'lib' if len(libs) == len(ts) else 'host' if not libs else 'mixed'
        projects = ','.join(sorted({proj(t) for t in libs}))
        names = ','.join(t.split('.')[-1] for t in libs) if group == 'mixed' else ''
        f.write(f'{path}\t{group}\t{projects}\t{names}\n')

# ── Tables ───────────────────────────────────────────────────────────────────────────────────────
md = []


def short(t):
    return t.replace('OpenFPS.Client.AudioEngine.', '').replace('OpenFPS.Client.Core.AudioEngine.', '') \
        .replace('OpenFPS.Client.Core.', '').replace('OpenFPS.Common.', '').replace('OpenFPS.', '')


def fileshort(f):
    return f.replace('OpenFPS.Client.Core/', 'CC/').replace('OpenFPS.Common/', 'Common/')


def label(t):
    g, p, _ = GROUP[t]
    return f'{g}:{p}' if p else g


md.append('## T1. Counts by group\n')
md.append('Types in the universe (declared in a sound, acoustics or geometry file, or referenced from one), by group and destination.\n')
md.append('| Group | Project | Types | Lines | Files |')
md.append('|---|---|---:|---:|---:|')
agg = collections.defaultdict(lambda: [0, 0, set()])
for t in universe:
    g, p, _ = GROUP[t]
    k = (g, p)
    agg[k][0] += 1
    agg[k][1] += int(TYPES[t]['lines'])
    for f in TYPES[t]['files'].split(';'):
        agg[k][2].add(f)
for (g, p), (n, l, fs) in sorted(agg.items()):
    md.append(f'| {g} | {p or "-"} | {n} | {l} | {len(fs)} |')
tot = [sum(v[0] for v in agg.values()), sum(v[1] for v in agg.values())]
md.append(f'| all | | {tot[0]} | {tot[1]} | |')
md.append('')
if unsorted:
    md.append('UNSORTED types (no rule matched): ' + ', '.join(sorted(unsorted)) + '\n')

# Per-file listing
md.append('## T2. Every type in the universe, by file\n')
md.append('Group a = library (with its project), b = host, c = a value both need (lives in the library). '
          'Lines are the declaration spans. A file whose types fall in more than one group has one row per group.\n')
md.append('| File | Group | Lines | Types | Note |')
md.append('|---|---|---:|---|---|')
rows = collections.defaultdict(list)
for t in universe:
    rows[(TYPES[t]['file'], label(t), GROUP[t][2])].append(t)
for (f, lab, note), ts in sorted(rows.items()):
    lines = sum(int(TYPES[t]['lines']) for t in ts)
    names = ', '.join(sorted(t.split('.')[-1] for t in ts))
    if len(names) > 220:
        names = names[:217] + '...'
    md.append(f'| {fileshort(f)} | {lab} | {lines} | {names} | {note} |')
md.append('')

# Crossings summary
md.append('## T3. Library to host: references that cross the boundary\n')
md.append(f'{len(crossings)} references from library (a or c) types to host (b) types, '
          f'in {len({e["file"] for e in crossings})} files. Full list with every line: crossings.tsv.\n')
md.append('### By host type\n')
md.append('| Host type | References | From library files |')
md.append('|---|---:|---|')
byto = collections.defaultdict(list)
for e in crossings:
    byto[e['to_type']].append(e)
for t, es in sorted(byto.items(), key=lambda kv: -len(kv[1])):
    fs = collections.Counter(fileshort(e['file']) for e in es)
    md.append(f'| {short(t)} | {len(es)} | ' + ', '.join(f'{f} ({n})' for f, n in fs.most_common(6)) + (' ...' if len(fs) > 6 else '') + ' |')
md.append('')
md.append('### By library file (worst first), with the lines\n')
md.append('| Library file | Refs | To | Lines |')
md.append('|---|---:|---|---|')
byfile = collections.defaultdict(list)
for e in crossings:
    byfile[e['file']].append(e)
for f, es in sorted(byfile.items(), key=lambda kv: -len(kv[1])):
    tos = collections.Counter(short(e['to_type']) for e in es)
    lines = sorted({int(e['line']) for e in es})
    ls = ', '.join(str(x) for x in lines[:14]) + (f' ... ({len(lines)} lines)' if len(lines) > 14 else '')
    md.append(f'| {fileshort(f)} | {len(es)} | ' + ', '.join(f'{k} {n}' for k, n in tos.most_common(5)) + f' | {ls} |')
md.append('')

md.append('### By the fix each needs\n')
FIX = [
    ('the world input (solids, sources, listener) instead of snapshots and entities',
     {'WorldSnapshot', 'EntitySnapshot', 'Networking.EntityDefinition', 'Components.Transform', 'Components.ColliderComponent',
      'Components.AcousticComponent', 'Components.MaterialComponent', 'Components.EntityType', 'Components.IdentityComponent',
      'Components.SoundEmitterComponent', 'Components.ColliderShape'}),
    ('rooms and openings as library values instead of RegionComponent and PortalComponent',
     {'Components.RegionComponent', 'Components.PortalComponent'}),
    ('library code calling back into open-fps integration classes', {'WorldAudioPlayer', 'SpatialService', 'NamedPlaces'}),
    ('small: a constant, a level or a codec passed in instead of read', {'Speech', 'PhysicsConstants', 'RoadData', 'Platform.VoiceCodec',
                                                                          'Networking.EntityState'}),
]
md.append('| Fix | References | Files |')
md.append('|---|---:|---:|')
seen = set()
for title, names in FIX:
    es = [e for e in crossings if short(e['to_type']) in names]
    seen.update(id(e) for e in es)
    md.append(f'| {title} | {len(es)} | {len({e["file"] for e in es})} |')
rest = [e for e in crossings if id(e) not in seen]
if rest:
    md.append(f'| other: ' + ', '.join(sorted({short(e["to_type"]) for e in rest})) + f' | {len(rest)} | {len({e["file"] for e in rest})} |')
md.append('')

# Values to move
md.append('## T4. Values both need (group c): where they are and where they go\n')
md.append('| Type | Now in | Goes to | Library refs | Host refs | Note |')
md.append('|---|---|---|---:|---:|---|')
for t in sorted(t for t in universe if GROUP[t][0] == 'c'):
    lr = sum(1 for e in EDGES if e['to_type'] == t and e['from_type'] in GROUP and lib(e['from_type']) and e['from_type'] != t)
    hr = sum(1 for e in EDGES if e['to_type'] == t and e['from_type'] in GROUP and GROUP[e['from_type']][0] == 'b')
    md.append(f'| {short(t)} | {fileshort(TYPES[t]["file"])} | {GROUP[t][1]} | {lr} | {hr} | {GROUP[t][2]} |')
md.append('')

# Layering
md.append('## T5. Inside the library: references against the proposed layering\n')
md.append('Layers: Geometry < Acoustics < Sound < Audio, and Native used only by Audio. Each row is a lower project '
          'using a higher one; each must be inverted or the type moved down before the projects can be split.\n')
md.append('| From (project) | To (project) | Refs | Example lines |')
md.append('|---|---|---:|---|')
lay = collections.defaultdict(list)
for e in layering:
    lay[(e['from_type'], e['to_type'])].append(e)
for (a, b), es in sorted(lay.items(), key=lambda kv: (-len(kv[1]))):
    ex = ', '.join(sorted({f"{fileshort(e['file'])}:{e['line']}" for e in es})[:3])
    md.append(f'| {short(a)} ({proj(a)}) | {short(b)} ({proj(b)}) | {len(es)} | {ex} |')
md.append('')

# Who uses the library from outside
md.append('## T6. Host projects using the would-be library\n')
md.append('References from each host project into library projects (allowed direction; this is what each host will reference).\n')
md.append('| Host project | Geometry | Acoustics | Sound | Audio | Native |')
md.append('|---|---:|---:|---:|---:|---:|')
use = collections.defaultdict(collections.Counter)
for e in EDGES:
    a, b = e['from_type'], e['to_type']
    if b in GROUP and lib(b) and (a not in GROUP or GROUP[a][0] == 'b'):
        key = e['from_project'] + (' (adapters/integration)' if e['from_project'] == 'OpenFPS.Client.Core' else '')
        use[key][proj(b)] += 1
for k in sorted(use):
    c = use[k]
    md.append(f"| {k} | {c['Geometry']} | {c['Acoustics']} | {c['Sound']} | {c['Audio']} | {c['Native']} |")
md.append('')

# ── Statics ──────────────────────────────────────────────────────────────────────────────────────
refs = collections.defaultdict(list)
for r in SREFS:
    refs[r['symbol']].append(r)


def zone(r):
    if r['from_project'] in ('OpenFPS.Tests', 'OpenFPS.AudioLab'):
        return 'test'
    ft = r['from_type']
    if ft in GROUP and lib(ft):
        return 'lib'
    return 'host'


def inside(r, owner):
    return r['from_type'] == owner or r['from_type'].startswith(owner + '.')


static_rows = []
for st in STATICS:
    owner = st['owner']
    if owner not in GROUP or not lib(owner):
        continue
    if st['mutability'] in ('immutable', 'lock'):
        continue
    rs = refs[st['symbol']]
    rd_ = collections.Counter(zone(r) for r in rs if r['access'] == 'read')
    wr = collections.Counter(zone(r) for r in rs if r['access'] in ('write', 'mutate'))
    outside = [r for r in rs if r['access'] in ('write', 'mutate') and not inside(r, owner)]
    if st['threadstatic'] == 'True':
        cat = 'scratch'
    elif outside:
        cat = 'external'
    elif sum(wr.values()) == 0:
        cat = 'table' if st['mutability'] != 'assignable' and st['mutability'] != 'settable (computed)' else 'never written'
    elif st['mutability'].startswith('readonly'):
        cat = 'cache'
    else:
        cat = 'internal'
    writers = sorted({short(r['from_type']) + ('*' if zone(r) == 'test' else '') for r in outside})
    static_rows.append((st, cat, rd_, wr, writers))

cats = collections.Counter(r[1] for r in static_rows)
md.append('## T7. Static mutable state in the library\n')
md.append('Every static field or property of a library (a or c) type whose value can be reassigned, or whose '
          'contents can change (a static readonly collection, array or object). Categories: external = written from '
          'outside its own type (settings, levers, world state); internal = reassigned only by its own type '
          '(registries, lazy state, counters); cache = a readonly collection its own type fills; never written = '
          'assignable but only initialised (often from an environment variable); table = a collection or array '
          'filled once at start; scratch = [ThreadStatic] working buffers.\n')
md.append('| Category | Count |')
md.append('|---|---:|')
for c in ('external', 'internal', 'cache', 'never written', 'table', 'scratch'):
    md.append(f'| {c} | {cats[c]} |')
md.append('')


def kind_of(st):
    return st['mutability'].replace('readonly ref (contents mutable)', 'contents').replace('readonly array (contents mutable)', 'array') \
        .replace('settable (computed)', 'settable')


for cat, title in (('external', 'T7a. Written from outside their own type'), ('internal', 'T7b. Reassigned by their own type'),
                   ('cache', 'T7c. Caches their own type fills'), ('never written', 'T7d. Assignable, never written after start')):
    md.append(f'### {title}\n')
    md.append('Reads and writes are counted per reference: lib = library code, host = host projects, test = Tests and AudioLab. '
              '"*" marks a writer in Tests or AudioLab.\n')
    md.append('| Static | Kind | Declared | Reads lib/host/test | Writes lib/host/test | Writers outside its type |')
    md.append('|---|---|---|---|---|---|')
    rows_ = [r for r in static_rows if r[1] == cat]
    rows_.sort(key=lambda r: (-(r[3]['host'] + r[3]['test'] + len(r[4])), r[0]['symbol']))
    for st, _, rd_, wr, writers in rows_:
        w = ', '.join(writers)
        if len(w) > 140:
            w = w[:137] + '...'
        md.append(f"| {short(st['symbol'])} | {kind_of(st)} | {fileshort(st['file'])}:{st['line']} | "
                  f"{rd_['lib']}/{rd_['host']}/{rd_['test']} | {wr['lib']}/{wr['host']}/{wr['test']} | {w} |")
    md.append('')

for cat, title in (('table', 'T7e. Tables filled once'), ('scratch', 'T7f. Per-thread scratch')):
    names = sorted(short(r[0]['symbol']) for r in static_rows if r[1] == cat)
    md.append(f'### {title} ({len(names)})\n')
    md.append(', '.join(names) + '\n')

# The clock: AudioClock.Now is a computed static, so it is not in the table above.
md.append('## T8. Readers of the global clock (AudioClock.Now)\n')
md.append('| File | Group | Reads |')
md.append('|---|---|---:|')
clock = collections.Counter()
for e in EDGES:
    if e['to_type'] == 'OpenFPS.Common.AudioClock' and e['member'] == 'Now':
        clock[(e['file'], label(e['from_type']) if e['from_type'] in GROUP else e['from_project'])] += 1
for (f, g), n in sorted(clock.items(), key=lambda kv: (-kv[1], kv[0])):
    md.append(f'| {fileshort(f)} | {g} | {n} |')
md.append(f'\n{sum(clock.values())} reads in {len(clock)} files.\n')

# ── Assets ───────────────────────────────────────────────────────────────────────────────────────
md.append('## T9. Files, base directory and environment in the library\n')
md.append('### T9a. Files and directories\n')
md.append('| Where | Type | Kind | Code |')
md.append('|---|---|---|---|')
envs = collections.defaultdict(set)
printed = set()
for r in sorted(ASSETS, key=lambda r: (r['file'], int(r['line']))):
    ft = r['from_type']
    if ft not in GROUP or not lib(ft):
        continue
    if r['kind'] == 'Environment.GetEnvironmentVariable':
        for m in re.findall(r'"(OPENFPS_[A-Z0-9_]+|SA_[A-Z_]+)"?', r['snippet']):
            envs[fileshort(r['file'])].add(m)
        continue
    if r['kind'] == 'path literal' and not re.search(r'ASSETS|assets|\.(json|wav|ogg|flac|sofa|bank|bin|csv|cache|ir|raw)\b', r['snippet']):
        continue
    if r['kind'] == 'path literal' and any(x['file'] == r['file'] and x['line'] == r['line'] and x['kind'] != 'path literal' for x in ASSETS):
        continue
    if r['kind'].endswith('iplHRTFRelease') or (r['file'], r['line'], r['kind']) in printed:
        continue
    printed.add((r['file'], r['line'], r['kind']))
    code = r['snippet'].replace('|', '\\|')
    if len(code) > 140:
        code = code[:137] + '...'
    md.append(f"| {fileshort(r['file'])}:{r['line']} | {short(ft)} | {r['kind']} | `{code}` |")
md.append('')
md.append('### T9b. Environment variables read by library code\n')
md.append('| File | Variables |')
md.append('|---|---|')
for f in sorted(envs):
    md.append(f"| {f} | {', '.join(sorted(envs[f]))} |")
md.append(f'\n{sum(len(v) for v in envs.values())} variables in {len(envs)} files.\n')

# ── The split of the integration types, method by method ────────────────────────────────────────
md.append('## T10. The integration types, method by method\n')
md.append('For each type that holds both sound logic and open-fps\'s world: how many of its members touch a host type '
          '(snapshot, entity, component, session, network) and how many lines those members span. Members that touch '
          'none can move into the library as they are; the rest are where the adapter is cut out.\n')
md.append('| Type | Members | Lines | Members reading host | Their lines | Host refs | Most-read host types |')
md.append('|---|---:|---:|---:|---:|---:|---|')
SPLIT_TYPES = ['OpenFPS.Client.Core.ClientAudioSystem', 'OpenFPS.Client.Core.WorldAudioPlayer', 'OpenFPS.Client.Core.SpatialService',
               'OpenFPS.Client.Core.BirdLife', 'OpenFPS.Client.Core.RainSurvey', 'OpenFPS.Client.Core.RainField',
               'OpenFPS.Client.AudioEngine.Acoustics.AsyncAcousticWorker', 'OpenFPS.Client.AudioEngine.Acoustics.SpatialAcoustics',
               'OpenFPS.Client.AudioEngine.Acoustics.OpeningGraph', 'OpenFPS.Client.AudioEngine.Acoustics.CabinWalls',
               'OpenFPS.Client.AudioEngine.Acoustics.VehicleShadow', 'OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider',
               'OpenFPS.Client.Core.AudioEngine.SteamAudio.SteamAudioScene', 'OpenFPS.Common.Systems.AcousticVolumeGenerator',
               'OpenFPS.Common.AudioEmission', 'OpenFPS.Common.RoomAcoustics', 'OpenFPS.Common.OpeningRoutes']
mem_lines = {}
for m in MEMBERS:
    mem_lines[(m['type'], m['member'])] = int(m['end']) - int(m['start']) + 1
for t in SPLIT_TYPES:
    ms = {m for (tt, m) in mem_lines if tt == t}
    total_lines = sum(mem_lines[(t, m)] for m in ms)
    hostrefs = collections.Counter()
    hostmembers = set()
    for e in EDGES:
        if e['from_type'] == t and e['to_type'] in GROUP and GROUP[e['to_type']][0] == 'b' and e['to_type'] != t:
            hostrefs[short(e['to_type'])] += 1
            hostmembers.add(e['from_member'])
    hl = sum(mem_lines.get((t, m), 0) for m in hostmembers)
    top = ', '.join(f'{k} {n}' for k, n in hostrefs.most_common(4))
    md.append(f'| {short(t)} | {len(ms)} | {total_lines} | {len(hostmembers)} | {hl} | {sum(hostrefs.values())} | {top} |')
md.append('')

# ── Appendix: every crossing line ───────────────────────────────────────────────────────────────
md.append('## A1. Every library-to-host reference, by file and host type\n')
md.append('| Library file | Host type | Refs | Lines |')
md.append('|---|---|---:|---|')
byft = collections.defaultdict(list)
for e in crossings:
    byft[(e['file'], short(e['to_type']))].append(int(e['line']))
for (f, t), ls in sorted(byft.items(), key=lambda kv: (kv[0][0], -len(kv[1]))):
    md.append(f"| {fileshort(f)} | {t} | {len(ls)} | {', '.join(str(x) for x in sorted(set(ls)))} |")
md.append('')

with open(os.path.join(out, 'tables.md'), 'w', encoding='utf-8') as f:
    f.write('\n'.join(md) + '\n')
print(f'universe {len(universe)} types; crossings {len(crossings)}; layering {len(layering)}; statics {len(static_rows)} {dict(cats)}; unsorted {len(unsorted)}')
