# Matter: what things are made of, and how they act on each other

Cody's direction, 2026-10-10: every material in the game should interact with every other the way it does in
real life. Fire and weather are one example, water another. Wind must move things physically, and its sound
must come from the things it moves, never from a recording playing where nothing is making it. A thing a
player builds sounds the way its material, size and shape make it sound.

This is the design that fire, water, weather, wind, the gas stove, bump sounds and the editor's building all
sit on, so they are built on one foundation instead of each inventing its own. Nothing here is built yet
unless it says so. It extends the rules already kept everywhere: sound falls out of materials and geometry,
never per-map constants (memory: no special cases), and a sound is a model of the thing, not of the sound.

## Contents

1. The rule
2. Materials: one table of physical properties
3. Solids
4. Liquids
5. Gases and air
6. How they act on each other
7. Struck things: sound from material, size and shape
8. Things built from parts
9. Chemistry
10. Order of work
11. Open questions

## 1. The rule

- A material is a set of measured physical properties with a source for each. Everything a material does
  (how it sounds struck, what it lets through, how it burns, how wet it gets, how it breaks) is computed from
  those properties, not looked up in a per-behaviour table.
- A thing is geometry plus a material (or several, for a thing made of parts). Its size and shape matter as
  much as its material: a 2 cm aluminium cube and a 2 m aluminium sheet are the same stuff and sound nothing
  alike.
- Interactions are physical: energy, mass and heat move between things by the laws that move them. A fire
  goes out because water carried its heat away, not because a rule says water beats fire.
- Where a full simulation costs too much, a cheaper model of the same physics is used near nobody, and the
  full one near players. The cheap and the full must agree where they meet.

## 2. Materials: one table of physical properties

### 2.1 What exists

`OpenFPS.Common/AcousticRegistry.cs` holds about 24 materials (Generic, Wood, Metal, Fence, Concrete, Marble,
Carpet, Glass, Plastic, Grass, Audience, Dirt, Gravel, Brick, Asphalt, Tile, Foliage, Plaster, AcousticTile,
Rubber, Leather, BootRubber, Skin, Water). Each already carries absorption and transmission per band,
scattering, density, Young's modulus and a loss factor (internal damping), and whether it is porous. "Metal"
is steel. An unknown name falls back to Generic silently (memory: registry falls back silently).

### 2.2 What a material needs

Grouped by what uses them. Every value cited (engineering handbooks, MatWeb, Ashby's materials selection
charts, fire engineering references).

- Mechanical: density; Young's modulus; Poisson's ratio; loss factor, and how it changes with frequency (wood
  and plastics damp their high modes much faster than metals do); strength (when it cracks, bends, breaks);
  hardness (how a contact with it feels: a finger on glass is a short hard contact, on rubber a long soft one).
- Acoustic: absorption and scattering per band (have), transmission derived from the mechanical numbers for
  airtight panels (have, `WallTransmission`), porosity (have).
- Thermal: specific heat; thermal conductivity; melting point; and for fuels, the temperature at which it gives
  off burnable gas, its heat of combustion, how much of it there is per square metre, and its moisture.
- Water: whether it soaks water up, how much, and how fast it dries; what wetness does to it (heavier, burns
  poorly, sounds duller, grips less).
- Surface: roughness (friction, how a scrape sounds, how a reflection smears).

### 2.3 The periodic table, and why it is not enough on its own

Elements matter, but most of what a game world is made of is not an element. The properties that make a
sound or a fire come from structure as much as from composition:

- Carbon is graphite (soft, dull, grey) and diamond (the stiffest thing there is): same element, completely
  different sound and behaviour.
- Iron, cast iron and steel are almost the same atoms; steel rings, cast iron clanks.
- Glass and quartz are both silica; concrete, brick, wood, plastics, rubber and skin are compounds or
  composites whose properties come from how they are put together.

So the material table is a library in families, each entry with its properties:

- Elements and pure metals: aluminium, iron, copper, lead, tin, zinc, titanium, gold, silver, and so on.
- Alloys: steels (mild, stainless), cast iron, brass, bronze, aluminium alloys.
- Stone, ceramic and glass: granite, marble, sandstone, slate, concrete, brick, terracotta, porcelain, soda-lime
  glass, laminated and toughened glass.
- Wood, by species (oak, pine, maple, balsa, plywood, MDF), with the grain: wood is several times stiffer
  along the grain than across it, which is why a plank and a block of the same wood sound different.
- Polymers: PVC, nylon, acrylic, polycarbonate, polyethylene, rubber, foam.
- Natural and soft: soil by type, sand, clay, ice, snow, leather, fabric, flesh.
- Liquids and gases (sections 4 and 5).

The periodic table is the right starting point for chemistry (section 9) and for the elemental entries;
the library above is what the game's things are made of. Existing names stay valid and map to entries (Metal
to mild steel, and so on).

## 3. Solids

A solid is geometry (triangles, docs/GEOMETRY.md) plus its material. It has today: collision, footstep and
impact material, transmission per band, absorption and reflection. Still to add, all from the material:

- Heat: it warms, conducts, and above its pyrolysis temperature gives off burnable gas (the fire design,
  docs/FIRE.md "Fire that burns what is there", being written now).
- Strength: it cracks, bends or breaks under a force bigger than it can take. Glass breaking exists; walls,
  wood and furniture do not break yet.
- Wetness: rain and water soak in by its porosity; wet wood is heavier, slower to burn and duller to strike.
- Change over time: wood burns to char then ash; metal rusts slowly; ground can be dug (geometry stage 5).

## 4. Liquids

### 4.1 What exists

Water is sound and a budget, not a substance: rain falls (rate, drop size), run-off fills gutters, drains and
downpipes and keeps them running after the rain stops (`Runoff`: a timed reservoir per roof or street, the
rational method), puddles form on roads (wet tyres, spray), and creeks, fountains, sinks and shores are placed
sources (docs/RUNNING_WATER.md, docs/WAVES_AND_SHORES.md, docs/WET_ROADS.md).

Built 2026-10-10 (docs/RUNNING_WATER.md section 13), the cheap version of 4.2's first two points: every world
tile and every map on the survey works out once which way each 2 m cell drains (Priority-Flood and D8, from the
tile and a margin round it; a map's whole place at once) and stores it with the tile; the map joins its tiles,
keeps the hollows that hold water as ponds and puddles (a hollow a road holds back drains through its culvert),
and the rain on each surface runs off by TR-55's curve number down the lines with each surface's own travel
time (Manning's n per surface), plus groundwater base flow. Where enough gathers, a running-water voice is placed
automatically (a rivulet, a roadside ditch, a creek, water over paving); Magnolia has 749 lines and 4,152 voices.
Water can be added at a point (`GroundWaterSystem.AddWater`: a bucket, a hose, a burst main) and runs downhill
soaking in; `WetnessAt` and `WaterReaching` say how wet a place is and how much water is reaching a burning thing.
Not yet: the full shallow-water model near players (13.9 says how it plugs in), poured water heard, soil by
place, other liquids.

### 4.2 How it should work

- Gravity: water flows downhill at a speed set by the slope and the roughness of what it runs over (Manning's
  equation), collects in hollows (depression storage), and soaks into the ground by soil type and wetness
  (infiltration; land cover from the world tiles gives the soil).
- Cheap, everywhere: each world tile works out once, from its terrain, which way each spot drains and how much
  ground drains through it (flow accumulation). Rain then runs along those paths with the run-off timing
  already built. Creeks and ditches come out of the ground's shape and the rain instead of being placed.
- Full, near players: a shallow-water simulation on a local grid for water that is thrown, poured, burst or
  hosed, so it spreads, finds the low spots and runs away downhill.
- Sound follows the flow: running-water voices take their flow rate and depth from the simulation, the way
  they take them from a placed source's settings today.
- Other liquids (oil, fuel) use the same flow with their own density, viscosity and whether they mix with
  water. Burning fuel floats on water and spreads with it.
- Freezing and melting by temperature: ice, snow and slush (snow is not run-off today; melt would make it so).

## 5. Gases and air

### 5.1 What exists

Air is the one gas the game simulates: temperature, humidity and wind (the weather's fronts and gusts), the
air's absorption of sound (ISO 9613-1), the wind in your ears (`EarWind`). Nothing can be released into it.

### 5.2 How it should work

- Released gases as puffs that the wind carries, that rise if hot or light and sink if heavy, and that thin as
  they spread (Gaussian puffs; a few dozen near players). Smoke, steam, and fuel gases (natural gas is lighter
  than air and rises; propane is heavier and pools on the floor).
- Fuel gases burn only between their flammable limits (methane about 5 to 15 % in air): a leak in a shut
  kitchen can reach the range and go off from a spark; outdoors it blows away first.
- Smoke matters for fire (oxygen, choking players) and later for graphics; it hardly changes sound.
- Air bending sound: temperature and wind change with height, so sound curves up on a hot afternoon and back
  down to you on a still night or downwind (already on the realism list in docs/CODY_ASKS_2026-10-08.md).

### 5.3 Wind acts on things

Wind is a force, and its sound comes only from what it moves or passes:

- Leaves and branches (foliage), long grass, crops.
- Wires, railings and thin poles (aeolian tones: a cylinder sheds vortices at about 0.2 times the wind speed
  over its diameter, which is why wires sing).
- Gaps, edges and openings (whistles, door gaps, a window open a crack).
- Flags, tarps and anything loose: banging, flapping, rattling, a gate on its latch, a door swinging, litter
  skittering, a bin blown over.
- Your own ears and head (`EarWind`, exists).
- Fire: spread, flame lean, embers.
- Water: ripples and waves on open water (waves exist, docs/WAVES_AND_SHORES.md).

Nothing plays wind where nothing is there to be moved: an empty car park in a breeze is quiet apart from your
ears and the far trees.

## 6. How they act on each other

The pairings that matter for play and for sound, each by the physics that drives it:

- Water on fire: puts it out mostly by cooling. Heating water takes energy and turning it to steam takes more
  than five times as much again (2.26 MJ/kg), which pulls the fuel's surface below the temperature at which it
  gives off burnable gas, so the flames run out of fuel. The steam also pushes air away from the fuel, and wet
  fuel nearby needs far more heat to catch. On burning oil or grease, water flashes to steam and throws the
  burning oil out: water does not always put a fire out. Sound: sizzle and steam hiss where water meets hot
  fuel, the roar collapsing, the crackle thinning, smouldering ticks.
- Rain on fire: light rain on a big fire turns to steam before it reaches the fuel; heavy, steady rain soaks the
  fuel bed, stops the spread, then the fire.
- Weather on fire: wind (spread, flame lean, embers carried), humidity and rain (fuel moisture), heat (fuel
  drying out), lightning (ignition).
- Fire on weather: a big fire draws air in toward its base and its hot plume rises and gusts, locally and
  audibly. Modelled round big fires. Fire-made thunderclouds (pyrocumulonimbus) are regional and left out.
- Fire on solids: heats, chars, burns, cracks glass (windows exist in the house fire), weakens and collapses
  structures, melts plastics and some metals (aluminium at 660 C).
- Water on solids: wets them (heavier, duller, slippery, slower to burn), rusts iron over time, freezes in
  cracks.
- Wind on everything: section 5.3.
- Heat on water: boils (a pot on the stove), steams, evaporates puddles in the sun.
- Cold on water: ice, snow; on solids: brittleness, contraction ticks (a car's exhaust cooling ticks today as
  a sound; it should come from the metal cooling).

## 7. Struck things: sound from material, size and shape

### 7.1 The physics

When something is struck, it rings in its own vibration modes, each a frequency with its own decay:

- The frequencies come from the shape, the size and the material's stiffness over its density. For a given
  shape every mode scales as the speed of sound in the material, sqrt(E / rho), over the size: double the size
  and every note drops an octave; aluminium and steel have almost the same sqrt(E / rho), so the same cube
  rings at almost the same pitch in either, while lead (soft and heavy) rings far lower and dies at once.
  For thin things thickness matters too: a plate's notes go as its thickness over its span squared.
- The decay comes mostly from the material's internal damping (the loss factor), not its density: aluminium
  and steel have tiny loss factors and ring for seconds, glass less, wood and plastics much less, concrete,
  brick and rubber hardly at all. Radiation into the air, and what the thing rests on or is held by, add their
  own damping: a bell on a string rings, the same bell on a cushion clunks. Concrete and brick go "thud" because
  they are heavily damped, massive, and fixed to the ground, so almost nothing rings.
- Density sets how hard it is to get going: the same tap moves a heavy block less, so it is quieter.
- The striker sets which modes are heard: a fingertip is soft and stays in contact for a few milliseconds, so
  it excites only the low modes (a dull tap); a knuckle, a key or a metal rod is hard and short, so it reaches
  the high modes (a bright click or ping). This is Hertzian contact: the stiffness and mass of both things and
  the speed of the strike give the contact time and force.
- Shape sets the pattern of the notes: a bar's overtones are not a string's, a plate's are denser, a hollow
  shell (a can, a pipe, a bell) has its own. That pattern is most of what tells the ear "bar", "sheet", "box".

### 7.2 How it is synthesised

Modal synthesis, an established method (van den Doel, Kry and Pai, "FoleyAutomatic", SIGGRAPH 2001; O'Brien,
Shen and Gatchalian 2002; Bonneel et al. 2008). For each shape, its modes are worked out once (analytically for
bars, plates, blocks, tubes and shells; by finite elements for any other mesh, offline or when a builder saves
the shape). Because modes scale by sqrt(E / rho) over the size, one computation per shape serves every
material and every size. Playing a strike is then a few dozen damped resonators driven by the contact force:
cheap, a fraction of an engine voice. Where a strike hits on the shape changes how strongly each mode is
excited (strike a bar in the middle and its odd modes ring; near the end, all of them).

### 7.3 What it gives the game

- The builder: start with an aluminium cube of a chosen size; tap it with a finger and hear a short, bright,
  pitched tap that rings; make it bigger and the note drops; make it concrete and it thuds; make it a thin
  sheet and it wobbles and booms.
- Bumps (todo "Bump sounds": today always the same woofy thunk): a body walking or running into a wall, a
  door, a car or a fence is a strike with a soft, heavy striker (a person, about 70 kg, a long contact) and the
  struck thing's modes. A plaster wall gives a low thud with its studs; a glass door shakes in its frame and
  rattles at its latch and hinges (the door models already have play and rattle: memory "clack is a loose bar");
  a steel fence rings and buzzes; a car body booms and its panels rattle.
- Slaps, knocks (the knock on Shift+E today is fitted to one recording), thrown and dropped objects, a can kicked
  down the street, hail on different roofs, an object scraped along another (the same modes driven by friction
  instead of a strike).

### 7.4 Honest limits

Simple rigid things (bars, bells, cubes, sheets, pipes, glass) synthesise convincingly with this method.
Complex ones (a wall struck by a body, wood with its grain, a whole door) need care and fitting against
recordings used as the spec, never played: the footstep and door work showed how many rounds that can take
(memory: synthesis failures; doors need detail and level). Band balance is measured before anything is played.

## 8. Things built from parts

- A built thing is parts, each with its material and shape, joined.
- Joined parts ring together: a struck tabletop rings through its legs; a joint adds damping, and a loose joint
  rattles (a contact that opens and closes, the same nonlinearity as the door's loose bar).
- Individual parts make no sound of their own; the assembly makes the sound when it is touched, struck, moved
  or driven, and each part contributes where it is: a strike on the glass of a door is heard from the glass,
  the rattle from the latch.
- Sound sources on a model have a place, a size and a directivity on it (an engine under the bonnet, a horn's
  mouth, a mower's deck under the engine): docs/GEOMETRY.md stage 4 and the editor's models. Cars already have
  this (memory: machines as parts); most other things do not.

## 9. Chemistry

A chemistry layer is possible later, as data: reactions with their ingredients, conditions (temperature,
contact, mixing), energy released or taken, and products. Combustion is one reaction already being modelled
(fuel and oxygen to heat, gas and ash). Others with clear physical consequences and sounds: a metal in acid
(fizzing gas), sodium in water (heat, hydrogen, a bang), rust (slow), alloys made by melting metals together,
gas mixtures reaching their flammable range (section 5.2). The sound comes from the consequences (gas
released, heat, a bang), not from the reaction itself. Full chemistry is enormous; the layer would start with a
small set of reactions that matter to play and grow from there.

## 10. Order of work

Agreed direction with Cody, 2026-10-10. Each step uses the one material table.

1. The material table: the properties in 2.2 for every existing material, with sources, and the families in
   2.3 started (common metals, alloys, stone and glass, woods, polymers, soils). Existing names keep working.
2. Struck things (section 7) for simple shapes, with a tap and knock in the builder, then bumps against walls,
   glass doors, fences and cars; fitted against recordings as the spec.
3. Fire driven by fuel, with water and rain putting it out and the fire's own local wind (being built now:
   docs/FIRE.md).
4. Water draining over terrain, the cheap per-tile version (section 4.2), with the world tiles. Built
   2026-10-10, approved by ear the same day (docs/RUNNING_WATER.md 13; inbox/water-over-terrain-2026-10-10).
5. Weather as a system (todo item 11): rain, wind, wet roads, fuel moisture and fire tied together.
6. Wind acting on things (section 5.3): wires, gaps, flags, loose objects, all from the wind field.
7. Released gases (smoke, steam, fuel leaks), then chemistry.

## 11. Open questions

- How many materials to start the library with, and whether players can define new ones (properties typed in
  the editor, checked against physical limits).
- Wood's grain (direction-dependent stiffness): whether every wooden part carries a grain direction.
- How much of the shallow-water and gas simulation the server runs (authoritative) and how much clients run
  for sound only.
