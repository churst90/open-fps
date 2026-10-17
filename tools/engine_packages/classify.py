#!/usr/bin/env python3
"""Sorts every file and type of the engine into a proposed package (docs/ENGINE_PACKAGES.md) and counts the
references that cross between packages against the proposed layers.

Usage: classify.py REPO SURVEY_DIR OUT_DIR
Input: the TSVs tools/sound_boundary's Roslyn reader writes into SURVEY_DIR (types, edges, statics,
static_refs, assets). Output in OUT_DIR:
  packages_tables.md     every table the document quotes
  packages_files.tsv     every source file of the sorted projects, its package, lines and why
  packages_crossings.tsv every reference from a package to one its layer may not reference
  ratchet/               the four lists EnginePackagesTests holds (OpenFPS.Tests/EnginePackages): files.tsv,
                         types.tsv, layers.tsv and allowed.tsv

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
NEW = {'Environment', 'World', 'Agents', 'Editor'}
PLANNED = set()

# ── Files: (glob, package, why) ─────────────────────────────────────────────────────────────────────
# The first match wins. "by" says whose plan puts it there: this survey, the physics or the library survey.
P = 'physics stage 2'
L6 = 'library stage 6'
FILE_RULES = [
    # The projects that are packages already map to themselves.
    ('OpenFPS.Words/*', 'Words', ''), ('OpenFPS.Schema/*', 'Schema', ''), ('OpenFPS.Native/*', 'Native', ''),
    ('OpenFPS.Geometry/*', 'Geometry', ''), ('OpenFPS.Materials/*', 'Materials', ''), ('OpenFPS.Astronomy/*', 'Astronomy', ''),
    ('OpenFPS.Physics/*', 'Physics', ''), ('OpenFPS.Acoustics/*', 'Acoustics', ''), ('OpenFPS.Models/*', 'Models', ''),
    ('OpenFPS.Accessibility/*', 'Accessibility', ''),
    ('OpenFPS.World/*', 'World', ''),
    ('OpenFPS.Agents/*', 'Agents', ''),
    ('OpenFPS.Audio/*', 'Audio', ''),
    ('OpenFPS.Environment/*', 'Environment', ''),
    ('OpenFPS.Editor/*', 'Editor', ''),

    # ── Common ──
    ('OpenFPS.Common/AudioEvents.cs', 'Common', 'wire'),
    ('OpenFPS.Common/Components.cs', 'Common', 'wire and entity model'),
    ('OpenFPS.Common/DistantMotion.cs', 'Common', 'how sent states are carried between packets'),
    ('OpenFPS.Common/EntityAcoustics.cs', 'Common', 'entity adapter: rooms, openings and the sound\'s world input'),
    ('OpenFPS.Common/EntityEmission.cs', 'Common', 'entity adapter: where an entity sounds from'),
    ('OpenFPS.Common/EntityGeometry.cs', 'Common', 'entity adapter: geometry'),
    ('OpenFPS.Common/GroundProbeMemo.cs', 'game', 'physics survey: a server cache'),
    ('OpenFPS.Common/Messages.cs', 'Common', 'wire'),
    ('OpenFPS.Common/MoverPoses.cs', 'game', 'a server counter'),
    ('OpenFPS.Common/PhysicsConstants.cs', 'Common', 'the game\'s reach and bounds, the tick, the input limits (the body: BodyConstants)'),
    ('OpenFPS.Common/PhysicsUtils.cs', 'Common', 'entity adapter: the ground probes of a world and of a snapshot (the question: Footing)'),
    ('OpenFPS.Common/PhysicsViews.cs', 'Common', 'entity adapter: a world and a snapshot as the physics sees them (IPhysicsWorld)'),
    ('OpenFPS.Common/Scopes.cs', 'game', 'the scope\'s maths and sway (what a scope is: OpenFPS.Models)'),
    ('OpenFPS.Common/StatePacking.cs', 'Common', 'wire'),
    ('OpenFPS.Common/UpdateThrottle.cs', 'Common', 'a fixed-rate gate'),
    ('OpenFPS.Common/WorldSnapshot.cs', 'Common', 'the client\'s world for one frame'),

    # ── Sound: what things are ──
    ('OpenFPS.Sound/*', 'Sound', ''),

    # ── Client.Core ──
    ('OpenFPS.Client.Core/Services/SoundMappingService.cs', 'Audio', 'which bank file a footstep plays'),
    ('OpenFPS.Client.Core/ClientAudioSystem.cs', 'game', 'the audio world\'s host: sources from the snapshot, the game\'s sound features, the place\'s name'),
    ('OpenFPS.Client.Core/WorldAudioPlayer.cs', 'game', 'the wire\'s one-off sounds decoded for the audio world\'s OneOffSounds'),
    ('OpenFPS.Client.Core/BeaconAids.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/DrivingAids.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/StairCues.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/Sightline.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/SightGrid.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/SightWatch.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/NamedPlaces.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/UiSounds.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/WallBumps.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/MapTracker.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/ListeningCalibration.cs', 'Accessibility', ''),
    ('OpenFPS.Client.Core/*', 'game', ''),

    # ── Server ──
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
    ('OpenFPS.Server/Core/TileStreamer.cs', 'game', 'host: World\'s tiles to each session'),
    ('OpenFPS.Server/Core/MapTemplates.cs', 'game', 'host'),
    ('OpenFPS.Server/OneWorld/WorldMaps.cs', 'game', 'host: sessions arriving in World\'s places'),
    ('OpenFPS.Server/OneWorld/WorldPlaces.cs', 'game', 'host: places laid on World\'s tiles as entities'),
    ('OpenFPS.Server/Repositories/PrefabRepository.cs', 'game', 'host: World\'s prefabs made into entities'),
    ('OpenFPS.Server/*', 'game', ''),
]

# Types that do not go where their file goes.
TYPE_RULES = {
    'OpenFPS.Server.Systems.ParkState': 'Agents',
    'OpenFPS.Server.Systems.ParkPhase': 'Agents',
    'OpenFPS.Server.Systems.ParkingSpot': 'Agents',
}

SORTED_PROJECTS = ('OpenFPS.Words', 'OpenFPS.Schema', 'OpenFPS.Native', 'OpenFPS.Geometry', 'OpenFPS.Materials', 'OpenFPS.Astronomy',
                   'OpenFPS.Physics', 'OpenFPS.Environment', 'OpenFPS.Acoustics', 'OpenFPS.Sound', 'OpenFPS.Models', 'OpenFPS.Audio', 'OpenFPS.World',
                   'OpenFPS.Common', 'OpenFPS.Agents',
                   'OpenFPS.Accessibility', 'OpenFPS.Editor',
                   'OpenFPS.Client.Core', 'OpenFPS.Server')
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
    for pre in ('OpenFPS.Geometry/', 'OpenFPS.Materials/', 'OpenFPS.Acoustics/', 'OpenFPS.Schema/', 'OpenFPS.Physics/',
                'OpenFPS.Models/', 'OpenFPS.World/', 'OpenFPS.Environment/'):
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

# ── The ratchet's lists (EnginePackagesTests) ──────────────────────────────────────────────
# What the test holds, with nothing in them that changes when a file is only edited: no line counts.
ratchet = os.path.join(out, 'ratchet')
os.makedirs(ratchet, exist_ok=True)
with open(os.path.join(ratchet, 'files.tsv'), 'w', encoding='utf-8', newline='\n') as f:
    f.write('file\tpackage\twhy\n')
    for path in sorted(FILES):
        pkg, why = file_rule(path)
        f.write(f'{path}\t{pkg}\t{why}\n')
with open(os.path.join(ratchet, 'types.tsv'), 'w', encoding='utf-8', newline='\n') as f:
    f.write('type\tpackage\n')
    for t in sorted(TYPE_RULES):
        f.write(f'{t}\t{TYPE_RULES[t]}\n')
with open(os.path.join(ratchet, 'layers.tsv'), 'w', encoding='utf-8', newline='\n') as f:
    f.write('package\tlayer\tmay_reference\n')
    for p in sorted(ALLOWED, key=lambda p: (LAYER[p], p)):
        f.write(f'{p}\t{LAYER[p]}\t{",".join(sorted(ALLOWED[p]))}\n')
by_file = collections.Counter((e['file'], PKG[e['to_type']]) for e in crossings)
with open(os.path.join(ratchet, 'allowed.tsv'), 'w', encoding='utf-8', newline='\n') as f:
    f.write('file\tto_package\tcount\n')
    for (path, to), n in sorted(by_file.items()):
        f.write(f'{path}\t{to}\t{n}\n')
print(f'files {len(FILES)}, unsorted {len(unsorted)}, crossings {len(crossings)}')
