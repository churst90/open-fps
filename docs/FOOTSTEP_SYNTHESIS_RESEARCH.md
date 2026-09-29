# Synthesising footsteps: what the literature says (2026-09-28)

Gathered after three attempts failed by ear (todo.md, "Synthetic footsteps, parked"). Sources were
read unless marked. The game uses the recorded bank; this is what a fourth attempt would follow.

## The established method

Every serious footstep synthesiser (Turchet 2016; Turchet, Serafin, Dimitrov and Nordahl, DAFx-10;
Nordahl, Turchet and Serafin, IEEE TVCG 2011; Cook, "Modeling Bill's Gait", AES 22nd Conference
2002) has the same two halves:

1. **An exciter: the force of the foot on the ground.** Heel event, then toe event, at most 150 ms
   apart; the minimum step period about 210 ms walking, 140 ms running (Turchet 2016, after
   Nilsson and Thorstensson). Hard soles have a sharp heel attack, soft soles a smooth one; running
   steps are shorter with higher peaks. Below about 500 Hz the sound comes from the force normal to
   the floor, above it from the friction force along it (Ekimov and Sabatier, JASA 2006). Every step
   redraws every value: a repeated step sounds mechanical.

2. **A surface model the exciter drives.**
   - **Hard floors (concrete, tile, wood, metal):** the shoe as a hammer striking a few modes of the
     floor through the Hunt-Crossley contact force f = k x^a (1 + mu dx/dt) (Avanzini and Rocchesso,
     DAFx-01). The contact time, (m/k)^(1/(a+1)), sets how bright the attack is, which is what
     listeners judge sole hardness by. Noise appears only as a short burst at the heel and toe
     attacks, in proportion to the exciter; a creaking floor adds a friction model. Designed DRY,
     reverb added after: wood with the room baked in was heard as concrete (TVCG 2011).
   - **Loose ground (gravel, sand, snow, leaves, grass):** Cook's PhISEM. A decaying energy
     variable; on each collision (probability N/1024 per sample) energy is added and each resonator's
     frequency is redrawn by +-11-18 %; the output is energy x noise through two-pole resonators.
     Around 1,000 collisions a second at peak for gravel, each well under a millisecond: a texture,
     not audible clicks. Grains come mainly while the foot's load is rising (Visell et al. 2009:
     rate proportional to dF/dt). Each surface is two or three layers:
     - gravel: one layer per stone size (large gravel resonance ~6.5 kHz, small ~9-12.7 kHz; Cook);
     - sand: one quiet, dull layer;
     - snow: a PhISEM layer for the foot sinking (the STK "crunch" preset, ~800 Hz) plus a crumpling
       layer for the snow breaking (Fontana and Bresin 2003);
     - dry leaves: two long layers and a sparse crunchy one;
     - grass, underbrush, dirt with pebbles: three layers, one a low thud.

No paper publishes per-material impact parameters or per-surface PhISEM settings beyond Cook's
gravel and the STK presets; they are tuned by measurement and by ear.

## How well it works

- Real recordings of people's own steps (Giordano et al., JASA 2012): hard and loose ground are
  almost never confused, but within a category listeners are right 27-51 % of the time (chance 25 %).
- Synthesised (Serafin et al., EuroHaptics 2010, 20 answers each): creaking wood 95 %, snow 75 %,
  metal 65 %, wood 50 %, gravel 40 %, beach sand 30 %, forest floor 30 %, dry leaves 25 %. Loose
  surfaces are the hard ones.

## Why the three attempts failed, against this

- "Watery": independent noise in each third-octave band breaks the fact that one impact or grain is
  broadband and hits every band at once (McDermott and Simoncelli, Neuron 2011: cross-band envelope
  correlation is what makes a texture sound natural).
- "Snare drum, no depth": a decaying noise tail is a snare's wires; the literature puts noise only at
  the attacks, gives the body to a few modes, and has a normal-force band under 500 Hz. The room was
  also fitted into the dry step.
- "Static, all the same": sparse, separately audible clicks are the wrong regime (gravel is ~1,000
  sub-millisecond grains a second), and one layer with one grain shape cannot tell gravel from sand
  from snow.

## If it is tried again

1. Exciter first: heel and toe events per step, from gait and speed, redrawn each step.
2. Hard floors: Hunt-Crossley hammer on a few modes, attack noise in proportion to the exciter, a
   friction layer for creaking wood, rendered dry.
3. Loose ground: PhISEM layers per surface, rate from dF/dt, every grain redrawing its frequency.
4. Measure before listening: band balance against dry reference steps (the first 30-60 ms), grain
   rate by Cook's 5.5-11 kHz peak count.
5. Listen to sequences of six or more steps, A/B against recordings at matched loudness.
