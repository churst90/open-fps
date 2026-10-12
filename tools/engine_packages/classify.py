#!/usr/bin/env python3
"""Sorts every file and type of the engine into a proposed package (docs/ENGINE_PACKAGES.md) and counts the
references that cross between packages against the proposed layers.

Usage: classify.py REPO SURVEY_DIR OUT_DIR
Input: the TSVs tools/sound_boundary's Roslyn reader writes into SURVEY_DIR (types, edges, statics,
static_refs, assets). Output in OUT_DIR:
  packages_tables.md     every table the document quotes
  packages_files.tsv     every source file of the sorted projects, its package, lines and why
  packages_crossings.tsv every reference from a package to one its layer may not reference

A file goes to the package of the first FILE_RULES pattern it matches; a type goes to its TYPE_RULES entry,
else to its file's package. A file no rule matches is reported as UNSORTED, so a new file cannot slip past.
'game' is open-fps itself: the server, the client's session and heads, which may reference anything.
Standard library only; run with python3 -I.
"""
import collections
import csv
import fnmatch
import os
import sys

repo = sys.argv[1] if len(sys.argv) > 1 else '.'
src = sys.argv[2] if len(sys.argv) > 2 else os.path.expanduser('~/.cache/openfps-build/pkgsurvey/out')
out = sys.argv[3] if len(sys.argv) > 3 else src


def rd(name):
    with open(os.path.join(src, name), encoding='utf-8') as f:
        return list(csv.DictReader(f, delimiter='\t', quoting=csv.QUOTE_NONE))


TYPES = {r['type']: r for r in rd('types.tsv')}
EDGES = rd('edges.tsv')
STATICS = rd('statics.tsv')
SREFS = rd('static_refs.tsv')
ASSETS = rd('assets.tsv')

# ── The packages and their layers ──────────────────────────────────────────────────────────────────
# What each package may reference (besides itself and the runtime). The layers are this graph's order.
LOW = ['Words', 'Schema', 'Native', 'Geometry', 'Materials']
ALLOWED = {
    'Words': set(),
    'Schema': set(),
    'Native': set(),
    'Geometry': set(),
    'Materials': set(),
    'Astronomy': {'Words'},
    'Physics': {'Geometry', 'Schema', 'Materials'},
    'Acoustics': {'Geometry', 'Materials'},
    'Environment': {'Geometry', 'Materials', 'Physics', 'Astronomy', 'Words', 'Schema'},
    'Sound': {'Geometry', 'Materials', 'Acoustics', 'Physics', 'Schema', 'Environment'},
    'Models': {'Geometry', 'Materials', 'Acoustics', 'Physics', 'Schema', 'Environment', 'Sound'},
    'Audio': {'Geometry', 'Materials', 'Acoustics', 'Physics', 'Schema', 'Environment', 'Sound', 'Models', 'Native'},
    'World': {'Geometry', 'Materials', 'Physics', 'Schema', 'Environment', 'Models', 'Words', 'Astronomy', 'Acoustics',
              'Sound'},
    'Common': {'Geometry', 'Materials', 'Physics', 'Schema', 'Environment', 'Models', 'Words', 'Astronomy',
               'Acoustics', 'Sound', 'World'},
    'Agents': {'Geometry', 'Materials', 'Physics', 'Schema', 'Environment', 'Models', 'Words', 'World', 'Sound'},
    'Accessibility': {'Geometry', 'Materials', 'Physics', 'Schema', 'Environment', 'Models', 'Words', 'Acoustics',
                      'Sound', 'Audio', 'World', 'Agents'},
    'Editor': {'Geometry', 'Materials', 'Physics', 'Schema', 'Environment', 'Models', 'Words', 'Acoustics', 'Sound',
               'World', 'Common', 'Accessibility'},
}
PACKAGES = list(ALLOWED) + ['game']


def layers():
    """Each package's layer: one more than the highest it may reference. Fails on a cycle."""
    level, busy = {}, set()

    def visit(p):
        if p in level:
            return level[p]
        if p in busy:
            raise SystemExit('cycle in ALLOWED through ' + p)
        busy.add(p)
        level[p] = 1 + max((visit(q) for q in ALLOWED[p]), default=-1)
        busy.discard(p)
        return level[p]
    for p in ALLOWED:
        visit(p)
    return level


LAYER = layers()
NEW = {'Materials', 'Environment', 'Models', 'World', 'Agents', 'Accessibility', 'Editor'}
PLANNED = {'Audio'}

# ── Files: (glob, package, why) ─────────────────────────────────────────────────────────────────────
# The first match wins. "by" says whose plan puts it there: this survey, the physics or the library survey.
P = 'physics stage 2'
L6 = 'library stage 6'
L7 = 'library stage 7 (host until split)'
FILE_RULES = [
    # The projects that are packages already map to themselves.
    ('OpenFPS.Words/*', 'Words', ''), ('OpenFPS.Schema/*', 'Schema', ''), ('OpenFPS.Native/*', 'Native', ''),
    ('OpenFPS.Geometry/*', 'Geometry', ''), ('OpenFPS.Astronomy/*', 'Astronomy', ''),
    ('OpenFPS.Physics/*', 'Physics', ''), ('OpenFPS.Acoustics/*', 'Acoustics', ''),

    # ── Common ──
    ('OpenFPS.Common/Ammunition.cs', 'Models', 'what a round and a fire selector are'),
    ('OpenFPS.Common/AudioEmission.cs', 'Common', 'entity adapter: where an entity sounds from'),
    ('OpenFPS.Common/AudioEvents.cs', 'Common', 'wire'),
    ('OpenFPS.Common/Beacons.cs', 'Accessibility', 'beacon categories and map policy'),
    ('OpenFPS.Common/Components.cs', 'Common', 'wire and entity model'),
    ('OpenFPS.Common/DirectionWords.cs', 'Words', 'bearings in words'),
    ('OpenFPS.Common/DistantMotion.cs', 'Common', 'how sent states are carried between packets'),
    ('OpenFPS.Common/DrivingCuePlanner.cs', 'Accessibility', 'the driver\'s cues, planned'),
    ('OpenFPS.Common/EntityAcoustics.cs', 'Common', 'entity adapter: rooms and openings'),
    ('OpenFPS.Common/EntityGeometry.cs', 'Common', 'entity adapter: geometry'),
    ('OpenFPS.Common/GroundProbeMemo.cs', 'game', 'physics survey: a server cache'),
    ('OpenFPS.Common/JsonConverters.cs', 'Common', 'wire'),
    ('OpenFPS.Common/LaneGuide.cs', 'Accessibility', 'the painted lines from the driver\'s seat'),
    ('OpenFPS.Common/LaneRoutes.cs', 'Agents', 'a vehicle\'s tour of lanes'),
    ('OpenFPS.Common/LineFollower.cs', 'Agents', 'a driver steering to a line'),
    ('OpenFPS.Common/Messages.cs', 'Common', 'wire'),
    ('OpenFPS.Common/MoverPoses.cs', 'game', 'a server counter'),
    ('OpenFPS.Common/PhysicsConstants.cs', 'Physics', P + ': the body and gravity; the tick stays in Common'),
    ('OpenFPS.Common/PhysicsUtils.cs', 'Physics', P + ': the probes; the World and snapshot overloads stay'),
    ('OpenFPS.Common/PlayerCoordinates.cs', 'Accessibility', 'coordinates as a player reads and types them'),
    ('OpenFPS.Common/RaceLine.cs', 'Agents', 'speeds along a line'),
    ('OpenFPS.Common/RoadMapData.cs', 'World', 'roads as sent to a driver'),
    ('OpenFPS.Common/Roads.cs', 'World', 'roads as a map declares them'),
    ('OpenFPS.Common/Scopes.cs', 'Models', 'what a scope is (ScopeMath and ScopeSway: game, by type)'),
    ('OpenFPS.Common/StatePacking.cs', 'Common', 'wire'),
    ('OpenFPS.Common/Speech/*', 'Models', 'the voices: who exists and what each can say'),
    ('OpenFPS.Common/Tiles.cs', 'World', 'tile detail and streaming radii'),
    ('OpenFPS.Common/TrackClearance.cs', 'World', 'a map\'s drive route checked at load'),
    ('OpenFPS.Common/UpdateThrottle.cs', 'Common', 'a fixed-rate gate'),
    ('OpenFPS.Common/WorldSnapshot.cs', 'Common', 'the client\'s world for one frame'),

    # ── Sound: the world's state and laws that are not sound ──
    ('OpenFPS.Sound/WeatherType.cs', 'Environment', 'wire'),
    ('OpenFPS.Sound/Precipitation.cs', 'Environment', 'what falls and its spectrum'),
    ('OpenFPS.Sound/Rain.cs', 'Environment', 'rain rates and drop sizes'),
    ('OpenFPS.Sound/Lightning.cs', 'Environment', 'strikes, their schedule and channel'),
    ('OpenFPS.Sound/Runoff.cs', 'Environment', 'rain on its way to the drains'),
    ('OpenFPS.Sound/GroundWater.cs', 'Environment', 'hydrology of the ground'),
    ('OpenFPS.Sound/RoadWater.cs', 'Environment', 'water on the road and its puddles'),
    ('OpenFPS.Sound/Fire.cs', 'Environment', 'what is burning and its shape: the fire\'s description, which its sound reads'),
    ('OpenFPS.Sound/FireSpread.cs', 'Environment', 'fire spreading through fuel'),
    ('OpenFPS.Sound/Fuel.cs', 'Environment', 'what a thing burns as'),
    ('OpenFPS.Sound/FireShape.cs', 'Environment', 'a fire\'s burning area'),
    ('OpenFPS.Sound/WorldWeather.cs', 'Environment', 'one world\'s weather state'),
    # ── Sound: what things are ──
    ('OpenFPS.Sound/Machines.cs', 'Models', 'machines as parts lists, the registry'),
    ('OpenFPS.Sound/ModelKindTable.cs', 'Models', 'the one table of model kinds'),
    ('OpenFPS.Sound/ModelLibrary.cs', 'Models', 'the models a map can name'),
    ('OpenFPS.Sound/Editing/ModelKinds.cs', 'Models', 'each kind\'s fields for the editor'),
    ('OpenFPS.Sound/Editing/FieldDescriptor.cs', 'Schema', 'a field as the editor types it'),
    ('OpenFPS.Sound/MachinePower.cs', 'Agents', 'how hard a driver works a machine: a notch, a power lever'),
    ('OpenFPS.Sound/AircraftGroundRun.cs', 'Agents', 'an aeroplane driven along its runway'),
    ('OpenFPS.Sound/WheelDynamics.cs', 'Physics', P + ': waits for the tyre split'),
    ('OpenFPS.Sound/*', 'Sound', ''),

    # ── Client.Core ──
    ('OpenFPS.Client.Core/AudioEngine/*', 'Audio', L6),
    ('OpenFPS.Client.Core/NearDrops.cs', 'Audio', L6),
    ('OpenFPS.Client.Core/RainField.cs', 'Audio', L6),
    ('OpenFPS.Client.Core/VoiceRegistry*.cs', 'Audio', 'the voices\' ids and kinds (audit P3)'),
    ('OpenFPS.Client.Core/LoudspeakerVoices.cs', 'Audio', 'programs rendered through their speakers'),
    ('OpenFPS.Client.Core/Services/SoundMappingService.cs', 'Audio', 'which bank file a footstep plays'),
    ('OpenFPS.Client.Core/ClientAudioSystem*.cs', 'game', L7),
    ('OpenFPS.Client.Core/WorldAudioPlayer.cs', 'game', L7),
    ('OpenFPS.Client.Core/BirdLife.cs', 'game', L7),
    ('OpenFPS.Client.Core/BeaconAids.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/DrivingAids.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/DrivingCueSounds.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/StairCues.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/TurnNarration.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/Sightline.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/SightGrid.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/SightWatch.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/NamedPlaces.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/ListMenu.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/UiSounds.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/WallBumps.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/ZoneSettle.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/MapTracker.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/ListeningCalibration.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/Platform/ISpeechOutput.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/Platform/SpeechDispatcherOutput.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/Platform/SpeechLog.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/EditorDialog.cs', 'Editor', ''),
    ('OpenFPS.Client.Core/EditorKeys.cs', 'Editor', ''),
    ('OpenFPS.Client.Core/EditorValuePrompt.cs', 'Editor', ''),
    ('OpenFPS.Client.Core/BuildForm.cs', 'Editor', ''),
    ('OpenFPS.Client.Core/*', 'game', ''),

    # ── Server ──
    ('OpenFPS.Server/Water/Drainage.cs', 'Environment', 'one tile\'s drainage from its ground'),
    ('OpenFPS.Server/Water/DrainageNetwork.cs', 'Environment', 'a map\'s drainage from its tiles\''),
    ('OpenFPS.Server/Systems/GroundWaterSystem.cs', 'Environment', 'the water on the ground, by map'),
    ('OpenFPS.Server/Systems/VehicleSystem.Following.cs', 'Agents', 'the gap to the car in front (IDM): extract from the partial class'),
    ('OpenFPS.Server/Systems/VehicleSystem.Junctions.cs', 'Agents', 'who goes first: extract from the partial class'),
    ('OpenFPS.Server/Systems/VehicleSystem.Crosswalks.cs', 'Agents', 'where a walker crosses a road: extract'),
    ('OpenFPS.Server/Systems/VehicleSystem.Routes.cs', 'Agents', 'tours of lanes from the road network: extract'),
    ('OpenFPS.Server/Systems/VehicleSystem.StreetLife.cs', 'Agents', 'the street\'s random events: extract'),
    ('OpenFPS.Server/Core/TerrainBuilder.cs', 'World', 'grading the survey; its entity making is the host\'s'),
    ('OpenFPS.Server/Editor/EntitySettings.cs', 'Editor', ''),
    ('OpenFPS.Server/Editor/LiveState.cs', 'Editor', ''),
    ('OpenFPS.Server/Editor/MapOverlay.cs', 'Editor', ''),
    ('OpenFPS.Server/Editor/MapVersions.cs', 'Editor', ''),
    ('OpenFPS.Server/Editor/ModelCatalog.cs', 'Editor', 'the kinds the editor edits'),
    ('OpenFPS.Server/Editor/ModelStore.cs', 'Editor', ''),
    ('OpenFPS.Server/Editor/OverlayRoute.cs', 'Editor', ''),
    ('OpenFPS.Server/Core/BuildSession.cs', 'Editor', ''),
    # The server's hosts: they step a package or adapt the entities to it, and stay.
    ('OpenFPS.Server/Systems/VehicleSystem*.cs', 'game', 'host: steps Agents'),
    ('OpenFPS.Server/Systems/CrowdSystem.cs', 'game', 'host: steps Agents'),
    ('OpenFPS.Server/Systems/PedestrianSpeech*.cs', 'game', 'host: steps Agents (the lines are Speech)'),
    ('OpenFPS.Server/Systems/Haunts.cs', 'game', 'host: Agents once it reads a view'),
    ('OpenFPS.Server/Systems/DoorManners.cs', 'game', 'host: Agents once it reads a view'),
    ('OpenFPS.Server/Systems/CharacterSystem.cs', 'game', 'host: steps Agents'),
    ('OpenFPS.Server/Systems/Pavements.cs', 'game', 'host: Agents once it reads a view'),
    ('OpenFPS.Server/Systems/CrossingSystem.cs', 'game', 'host: steps Agents'),
    ('OpenFPS.Server/Systems/RailSystem.cs', 'game', 'host: steps Agents (physics survey: trains kinematic)'),
    ('OpenFPS.Server/Systems/WorldEnvironmentSystem.cs', 'game', 'host: steps Environment'),
    ('OpenFPS.Server/Systems/LightningSystem.cs', 'game', 'host: steps Environment'),
    ('OpenFPS.Server/Systems/RoadWaterSystem.cs', 'game', 'host: steps Environment'),
    ('OpenFPS.Server/Systems/FireSystem.cs', 'game', 'host: steps Environment'),
    ('OpenFPS.Server/Water/*', 'game', 'host: adapts the map to Environment'),
    ('OpenFPS.Server/Editor/*', 'game', 'host: the editor\'s commands over sessions and maps'),
    ('OpenFPS.Server/Core/MapTiles.cs', 'game', 'host: adapts entities to World\'s tiles'),
    ('OpenFPS.Server/Core/TerrainTiles.cs', 'game', 'host: World\'s terrain as entities'),
    ('OpenFPS.Server/Core/TerrainBuilder.cs', 'game', 'host: World\'s terrain as entities'),
    ('OpenFPS.Server/Core/TileStreamer.cs', 'game', 'host: World\'s tiles to each session'),
    ('OpenFPS.Server/Core/MapTemplates.cs', 'game', 'host'),
    ('OpenFPS.Server/OneWorld/WorldMaps.cs', 'game', 'host: sessions arriving in World\'s places'),
    ('OpenFPS.Server/OneWorld/WorldPlaces.cs', 'game', 'host: places laid on World\'s tiles as entities'),
    ('OpenFPS.Server/Repositories/PrefabRepository.cs', 'game', 'host: World\'s prefabs made into entities'),
    ('OpenFPS.Server/OneWorld/*', 'World', ''),
    ('OpenFPS.Server/Core/MapRoadsBuilder.cs', 'World', ''),
    ('OpenFPS.Server/Repositories/MapElevation.cs', 'World', ''),
    ('OpenFPS.Server/Repositories/MapRepository.cs', 'World', 'the map file format'),
    ('OpenFPS.Server/Repositories/PrefabTemplate.cs', 'World', 'the prefab format'),
    ('OpenFPS.Server/Repositories/PrefabValidator.cs', 'World', ''),
    ('OpenFPS.Server/Repositories/CompositeRepository.cs', 'World', 'composite templates and placements: map data'),
    ('OpenFPS.Server/*', 'game', ''),
]

# Types that do not go where their file goes.
TYPE_RULES = {
    'OpenFPS.Common.MaterialProperties': 'Materials',
    'OpenFPS.Common.AcousticRegistry': 'Materials',
    'OpenFPS.Common.Underfoot': 'Materials',
    'OpenFPS.Common.StrikeKind': 'Materials',
    'OpenFPS.Common.StrikeBehaviour': 'Materials',
    'OpenFPS.Common.RicochetFace': 'Materials',
    'OpenFPS.Common.RoadSurfaces': 'Materials',
    'OpenFPS.Common.ScopeMath': 'game',
    'OpenFPS.Common.ScopeSway': 'game',
    'OpenFPS.Common.WindWaves': 'Environment',
    'OpenFPS.Common.WaterBody': 'Environment',
    'OpenFPS.Common.GroundChannelKind': 'Environment',
    'OpenFPS.Server.Systems.ParkState': 'Agents',
    'OpenFPS.Server.Systems.ParkPhase': 'Agents',
    'OpenFPS.Server.Systems.ParkingSpot': 'Agents',
    'OpenFPS.Server.Systems.StreetLines': 'Agents',
    'OpenFPS.Server.Systems.SpeechConditions': 'Agents',
    'OpenFPS.Server.OneWorld.WorldPlace': 'World',
    'OpenFPS.Common.Components.EntityType': 'World',
    'OpenFPS.Common.Networking.Vector3Converter': 'Geometry',
    'OpenFPS.Common.Networking.QuaternionConverter': 'Geometry',
    'OpenFPS.Client.Core.Platform.GameKey': 'Accessibility',
    'OpenFPS.Client.Core.Input.KeyModifiers': 'Accessibility',
    'OpenFPS.Server.Core.SafeText': 'Words',
}

SORTED_PROJECTS = ('OpenFPS.Words', 'OpenFPS.Schema', 'OpenFPS.Native', 'OpenFPS.Geometry', 'OpenFPS.Astronomy',
                   'OpenFPS.Physics', 'OpenFPS.Acoustics', 'OpenFPS.Sound', 'OpenFPS.Common', 'OpenFPS.Client.Core',
                   'OpenFPS.Server')
USERS = ('OpenFPS.Server', 'OpenFPS.Client.Core', 'OpenFPS.Client', 'OpenFPS.Client.Gtk', 'OpenFPS.AudioLab',
         'OpenFPS.Tests')
SKIP = {'OpenFPS.Tests', 'OpenFPS.AudioLab'}


def file_rule(path):
    for pat, pkg, why in FILE_RULES:
        if fnmatch.fnmatchcase(path, pat):
            return pkg, why
    return None, ''


# Every source file of the sorted projects, from disk (a file with no type is still a file).
FILES = {}
for proj in SORTED_PROJECTS:
    for root, dirs, files in os.walk(os.path.join(repo, proj)):
        dirs[:] = [d for d in dirs if d not in ('obj', 'bin')]
        for fn in files:
            if fn.endswith('.cs'):
                p = os.path.join(root, fn)
                rel = os.path.relpath(p, repo).replace(os.sep, '/')
                with open(p, encoding='utf-8', errors='replace') as fh:
                    n = sum(1 for _ in fh)
                FILES[rel] = n
unsorted = sorted(f for f in FILES if file_rule(f)[0] is None)


def pkg_of_type(t):
    if t in TYPE_RULES:
        return TYPE_RULES[t]
    r = TYPES.get(t)
    if r is None:
        return None
    if r['project'] not in SORTED_PROJECTS:
        return 'user'  # the lab, the tests, the heads
    return file_rule(r['file'])[0]


PKG = {t: pkg_of_type(t) for t in TYPES}
for t in TYPE_RULES:
    if t not in TYPES:
        print('type rule names no type: ' + t, file=sys.stderr)


def from_pkg(e):
    """The package a reference is made from: its type's rule, else the file it sits in (so a partial class
    splits by file, as an extraction would split it)."""
    if e['from_type'] in TYPE_RULES:
        return TYPE_RULES[e['from_type']]
    if e['from_project'] not in SORTED_PROJECTS:
        return 'user'
    return file_rule(e['file'])[0]

# The wire hash today (OpenFPS.Common.csproj): Common, and WireLibrarySource.
def hashed(path):
    if path.startswith('OpenFPS.Common/'):
        return True
    for pre in ('OpenFPS.Geometry/', 'OpenFPS.Acoustics/', 'OpenFPS.Schema/', 'OpenFPS.Physics/'):
        if path.startswith(pre):
            return True
    return path.startswith('OpenFPS.Sound/') and not path.startswith('OpenFPS.Sound/Core/')


lines_md = []


def w(s=''):
    lines_md.append(s)


def short(t):
    return t.rsplit('.', 1)[-1]


# ── Files ───────────────────────────────────────────────────────────────────────────────────────────
with open(os.path.join(out, 'packages_files.tsv'), 'w', encoding='utf-8') as f:
    f.write('file\tlines\tpackage\twhy\thashed\ttypes\n')
    tbyfile = collections.defaultdict(list)
    for t, r in TYPES.items():
        tbyfile[r['file']].append(t)
    for path in sorted(FILES):
        pkg, why = file_rule(path)
        ts = sorted(tbyfile.get(path, []))
        moved = [f'{short(t)}->{TYPE_RULES[t]}' for t in ts if t in TYPE_RULES]
        if moved:
            why = (why + '; ' if why else '') + 'by type: ' + ', '.join(moved)
        f.write(f'{path}\t{FILES[path]}\t{pkg}\t{why}\t{"yes" if hashed(path) else "no"}\t{",".join(short(t) for t in ts)}\n')

# ── The layers ─────────────────────────────────────────────────────────────────────────────────────
w('## The layers')
w()
w('A package\'s layer is one more than the highest layer it may reference; the game is above them all.')
w()
w('| Layer | Package | May reference |')
w('|---:|---|---|')
for p in sorted(ALLOWED, key=lambda p: (LAYER[p], PACKAGES.index(p))):
    deps = sorted(ALLOWED[p], key=lambda q: (LAYER[q], PACKAGES.index(q)))
    w(f'| {LAYER[p]} | {p} | {", ".join(deps) if deps else "nothing"} |')
w()

# ── Counts per package, by where it comes from ───────────────────────────────────────────────────────
w('## Packages: files, types and lines, by the project they are in today')
w()
w('Lines are whole files for the file rule; a type moved by a type rule counts its declaration span against its new '
  'package and is taken off its file\'s.')
w()
w('| Package | From | Files | Types | Lines |')
w('|---|---|---:|---:|---:|')
agg = collections.defaultdict(lambda: [set(), 0, 0])  # (pkg, project) -> files, types, lines
for path, n in FILES.items():
    pkg = file_rule(path)[0]
    proj = path.split('/')[0]
    agg[(pkg, proj)][0].add(path)
    agg[(pkg, proj)][2] += n
for t, r in TYPES.items():
    if r['project'] not in SORTED_PROJECTS:
        continue
    pkg = PKG[t]
    fpkg = file_rule(r['file'])[0]
    agg[(pkg, r['project'])][1] += 1
    if pkg != fpkg:
        agg[(pkg, r['project'])][2] += int(r['lines'])
        agg[(fpkg, r['project'])][2] -= int(r['lines'])
        agg[(pkg, r['project'])][0].add(r['file'] + '#type')
tot = collections.defaultdict(lambda: [0, 0, 0])
for pkg in PACKAGES:
    for proj in SORTED_PROJECTS:
        if (pkg, proj) in agg:
            fs, nt, nl = agg[(pkg, proj)]
            nf = len([x for x in fs if not x.endswith('#type')])
            w(f'| {pkg} | {proj.replace("OpenFPS.", "")} | {nf} | {nt} | {nl} |')
            tot[pkg][0] += nf
            tot[pkg][1] += nt
            tot[pkg][2] += nl
w()
w('| Package | Kind | Files | Types | Lines |')
w('|---|---|---:|---:|---:|')
for pkg in PACKAGES:
    kind = 'new' if pkg in NEW else 'planned (library stage 6)' if pkg in PLANNED else 'stays in open-fps' if pkg == 'game' else 'exists'
    w(f'| {pkg} | {kind} | {tot[pkg][0]} | {tot[pkg][1]} | {tot[pkg][2]} |')
w()
if unsorted:
    w('UNSORTED files: ' + ', '.join(unsorted))
    w()

# ── References between packages ──────────────────────────────────────────────────────────────────────
matrix = collections.Counter()
crossings = []
for e in EDGES:
    if e['from_project'] in SKIP or e['from_project'] not in SORTED_PROJECTS:
        continue
    a, b = from_pkg(e), PKG.get(e['to_type'])
    if a is None or b is None or a == b or b == 'user':
        continue
    matrix[(a, b)] += 1
    if a != 'game' and b not in ALLOWED[a]:
        crossings.append(e)

with open(os.path.join(out, 'packages_crossings.tsv'), 'w', encoding='utf-8') as f:
    f.write('from_package\tto_package\tfrom_type\tfile\tline\tto_type\tmember\tfrom_member\n')
    for e in sorted(crossings, key=lambda e: (from_pkg(e), PKG[e['to_type']], e['file'], int(e['line']))):
        f.write('\t'.join([from_pkg(e), PKG[e['to_type']], e['from_type'], e['file'], e['line'], e['to_type'],
                           e['member'], e['from_member']]) + '\n')

w('## References from each package to the others (lab and tests left out)')
w()
w('A row is the package that refers; a column the package referred to. Counts in brackets are not allowed by the '
  'layers.')
w()
cols = [p for p in PACKAGES if any(matrix[(a, p)] for a in PACKAGES)]
w('| From | ' + ' | '.join(cols) + ' |')
w('|---|' + '---:|' * len(cols))
for a in PACKAGES:
    cells = []
    for b in cols:
        n = matrix[(a, b)]
        if not n:
            cells.append('')
        elif a != 'game' and b not in ALLOWED[a] and a != b:
            cells.append(f'({n})')
        else:
            cells.append(str(n))
    if any(cells):
        w(f'| {a} | ' + ' | '.join(cells) + ' |')
w()

w('## Crossings: references a package\'s layer does not allow')
w()
w(f'{len(crossings)} references. Every line: packages_crossings.tsv.')
w()
w('| From | To | Refs | Files | Worst types (refs) |')
w('|---|---|---:|---:|---|')
cx = collections.defaultdict(list)
for e in crossings:
    cx[(from_pkg(e), PKG[e['to_type']])].append(e)
for (a, b), es in sorted(cx.items(), key=lambda kv: (PACKAGES.index(kv[0][0]), -len(kv[1]))):
    tt = collections.Counter(short(e['to_type']) for e in es)
    w(f'| {a} | {b} | {len(es)} | {len({e["file"] for e in es})} | '
      + ', '.join(f'{k} {n}' for k, n in tt.most_common(6)) + ' |')
w()

w('### Crossings by file, worst first, with example lines')
w()
w('| From | To | File | Refs | To types | Lines |')
w('|---|---|---|---:|---|---|')
byfile = collections.defaultdict(list)
for e in crossings:
    byfile[(from_pkg(e), PKG[e['to_type']], e['file'])].append(e)
for (a, b, fpath), es in sorted(byfile.items(), key=lambda kv: (PACKAGES.index(kv[0][0]), -len(kv[1]))):
    tt = collections.Counter(short(e['to_type']) for e in es)
    ls = sorted({int(e['line']) for e in es})
    lstr = ', '.join(map(str, ls[:6])) + (f' ... ({len(ls)} lines)' if len(ls) > 6 else '')
    w(f'| {a} | {b} | {fpath} | {len(es)} | ' + ', '.join(f'{k} {n}' for k, n in tt.most_common(4)) + f' | {lstr} |')
w()

# ── Who uses each package ─────────────────────────────────────────────────────────────────────────
w('## Who references each package (references from outside it, by project)')
w()
w('| Package | ' + ' | '.join(u.replace('OpenFPS.', '') for u in USERS) + ' |')
w('|---|' + '---:|' * len(USERS))
use = collections.defaultdict(collections.Counter)
for e in EDGES:
    b = PKG.get(e['to_type'])
    a = from_pkg(e)
    if b is None or b == 'user' or a == b:
        continue
    use[b][e['from_project']] += 1
for p in PACKAGES:
    w(f'| {p} | ' + ' | '.join(str(use[p][u]) for u in USERS) + ' |')
w()

# ── Statics ───────────────────────────────────────────────────────────────────────────────────────
w('## Statics that can change, by package')
w()
w('Mutable: assignable, settable or a mutable collection. Written outside: some other type writes or mutates it.')
w()
w('| Package | Mutable statics | Thread-static | Written from outside their type |')
w('|---|---:|---:|---:|')
writes = collections.Counter()
for r in SREFS:
    owner = r['symbol'].rsplit('.', 1)[0]
    if r['access'] in ('write', 'mutate') and r['from_type'] != owner:
        writes[r['symbol']] += 1
spk = collections.defaultdict(list)
for s in STATICS:
    if s['mutability'] == 'immutable':
        continue
    p = PKG.get(s['owner'])
    if p is None or p == 'user':
        continue
    spk[p].append(s)
for p in PACKAGES:
    ss = spk[p]
    w(f'| {p} | {len(ss)} | {sum(1 for s in ss if s["threadstatic"] == "True")} | '
      f'{sum(1 for s in ss if writes[s["symbol"]])} |')
w()
w('### Statics written from outside their type, in the new packages')
w()
w('| Package | Static | Mutability | Outside writes | File:line |')
w('|---|---|---|---:|---|')
for p in PACKAGES:
    if p not in NEW:
        continue
    for s in sorted(spk[p], key=lambda s: -writes[s['symbol']]):
        if writes[s['symbol']]:
            w(f'| {p} | {".".join(s["symbol"].split(".")[-2:])} | {s["mutability"]} | {writes[s["symbol"]]} | {s["file"]}:{s["line"]} |')
w()

# ── Files and environment ──────────────────────────────────────────────────────────────────────────
w('## Files, folders and environment read or written, by package')
w()
w('| Package | Environment variables | Base directory | Working-directory or literal paths | File and folder calls |')
w('|---|---:|---:|---:|---:|')
apk = collections.defaultdict(collections.Counter)
envs = collections.defaultdict(set)
for a in ASSETS:
    p = PKG.get(a['from_type'])
    if p is None or p == 'user':
        continue
    k = a['kind']
    if k == 'Environment.GetEnvironmentVariable':
        apk[p]['env'] += 1
        import re
        m = re.search(r'"(OPENFPS_[A-Z0-9_]+)"', a['snippet'])
        if m:
            envs[p].add(m.group(1))
    elif 'BaseDirectory' in k:
        apk[p]['base'] += 1
    elif k == 'path literal':
        apk[p]['lit'] += 1
    elif k.startswith('File.') or k.startswith('Directory.') or k.startswith('Path.'):
        apk[p]['io'] += 1
for p in PACKAGES:
    c = apk[p]
    w(f'| {p} | {c["env"]} | {c["base"]} | {c["lit"]} | {c["io"]} |')
w()
w('| Package | Environment variables read |')
w('|---|---|')
for p in PACKAGES:
    if envs[p] and p != 'game':
        w(f'| {p} | {", ".join(sorted(envs[p]))} |')
w()

# ── The wire hash ──────────────────────────────────────────────────────────────────────────────────
w('## The wire hash today, by package')
w()
w('Files hashed into WireContract.Hash today (OpenFPS.Common.csproj: Common and WireLibrarySource).')
w()
w('| Package | Files | Hashed today | Not hashed |')
w('|---|---:|---:|---:|')
hp = collections.defaultdict(lambda: [0, 0])
for path in FILES:
    p = file_rule(path)[0]
    hp[p][0 if hashed(path) else 1] += 1
for p in PACKAGES:
    a, b = hp[p]
    if a + b:
        w(f'| {p} | {a + b} | {a} | {b} |')
w()

with open(os.path.join(out, 'packages_tables.md'), 'w', encoding='utf-8') as f:
    f.write('\n'.join(lines_md) + '\n')
print(f'files {len(FILES)}, unsorted {len(unsorted)}, crossings {len(crossings)}')
