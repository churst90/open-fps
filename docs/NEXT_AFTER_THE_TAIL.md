# What comes next (2026-09-19, after the tail was fixed)

> **SUPERSEDED IN PART by `docs/THE_CITY.md` (session 17, the same day).** Sections 1, 2, 3 and most
> of 6 below are done: the corridor comb was closed by ear, standing machines and aircraft have a
> voice and are on the map, footsteps were settled by measurement in favour of the bank, and traffic
> and aircraft run. Section 4 (the bus shelter) now has the gate test it demanded and a specified
> fix. Section 5 is untouched and is now the most audible open question on the map. The light rail
> is the one thing from section 6 still to do.

Approved by ear at the end of session 16: **rooms are distinct, and there is no popping.** The long
arc that ran from session 9 to here — "all the reflections sound the same, there's no real variation
at all" — is closed. What follows is what that arc was blocking.

---

## 0. What closed, and the one lesson worth carrying

Four separate faults sat in series, which is why each fix on its own changed nothing audible:

| Fault | What it did | Commit |
|---|---|---|
| A metering loop held the reverb unit at unity gain | cancelled 7 dB of liveness between a corridor and a car park | `158f02c` |
| The room equation assumed a cube | a flat 21×28×2.5 garage has 5× a cube's surface; the field was 9 dB too loud | `7604dce` |
| `TargetRegionId` defaulted to −1, and −1 **is** the outdoors | every footstep reverberated outdoors; each room got only the quarter-strength cross-send | `f43df69` |
| `EARLYLATEMIX = 0` | FMOD's scale is *late-to-early*: 0 is early reflections **only**. The unit rendered its fixed stamped pattern and **no tail at all**, in every room | `ce9ee2e` |

**The lesson:** for a whole session every measurement read correct and the ear said no, and both were
right. Three correct fixes landed upstream of a unit that had been told to render no tail. *Metering
the parameter is not metering the output.*

**The instrument that settles this class of question is `--tailcheck`.** It forces a decay onto the
unit, drops one footstep, meters every stage of the bus — what entered the unit, what left it, what
left the binaural stage, the fader, and how many sends are actually plugged in — then measures the
decay of the captured mix. Reach for it before re-deriving any acoustic model.

```
dotnet OpenFPS.AudioLab.dll --tailcheck decay=5107 [earlylate=N] [keepalive=on] [out=path]
```

---

## 1. The corridor comb — the one deliberately deferred

**Deferred pending Cody's ear, and his ear has now said the map is good** — so this may already be a
non-issue. Decide by listening before touching it.

In a 2.2 m corridor the near-field boundary probes and the step reflections both answer for the same
walls at the same ~6 ms delay. The city plan flagged the double-count from the start. It was left
alone on purpose: with the tail finally audible the balance against it changes, and pre-emptively
fixing something that may now be fine is how the last three sessions went wrong.

**How to know:** walk the corridor at `/tp 20.3 8 0.1`. If it sounds combed or metallic *now*, retire
the near-field probe contribution where an image-source arrival already covers the same surface. If it
sounds like a corridor, delete this section from the next plan.

---

## 2. Stationary machines: the voice path that does not exist

`SmallMachineSpec` and `SmallMachineSynth` are measured, scripted and approved by `--yard`. **Mowers
and air conditioners still cannot be placed in the game**, because nothing gives a voice to a machine
that stands still. A vehicle's engine gets one from `ClientAudioSystem`; a fixed machine gets none.

Needed: a render-pool voice with a place, an extent and a level — the treatment a vehicle's engine
already gets, minus the movement. Then a condenser on the garage roof and a mower behind the west
block.

**How to know:** stand across the street from the garage and hear the AC unit where the garage is;
walk past the mower and hear it pass. It must obey the same room the footsteps now do.

---

## 3. Footsteps from the model, or from the bank

`FootstepSpike` renders walks from foot and ground (`--footsteps`). `inbox/footsteps` is **empty** —
the unlabelled files Cody meant to sort are not there, so either they were cleared or they live
elsewhere; ask before assuming they were lost.

The decision is still Cody's: if the synthesised steps pass the ear, `SoundMappingService` resolves
footsteps from the model per material and shoe instead of from the file bank. If not, keep the
recordings and use the synth only for materials that have none.

---

## 4. The survey's known blind spot: a small enclosure inside a big one

A bus shelter on an open street surveys as the street. The rays leave the shelter and nothing tells
the survey they have. **Three fixes were tried and all three were reverted** (`git checkout HEAD --`):
median-keyed shrinking flattens real rooms (garage 5 s → 0.7 s), mean-free-path keying does not move a
shelter at all, and settling does both.

Do not attempt a fourth without a test that fails on the shelter *and* passes on the garage first.

---

## 5. The open modelling question

The tunnel measures 40% absorption where the textbook says 69%. The room equation currently uses
**enclosure** (`e/(1−e)`) rather than **measured absorption** (`(1−ᾱ)/ᾱ`). Now that the tail is
audible, this is finally testable by ear: the tunnel should be the longest, darkest space on the map
after the garage.

---

## 6. Then: the rest of the city

From `docs/NEXT_THE_CITY.md`, unchanged and still in this order:

1. **Traffic** — vehicles on the roads, which are already data.
2. **Aggregation** — many distant sources into one, so a city does not cost a voice per car.
3. **Rail** — the model is built and approved (`docs/TRAINS.md`); it has never been on a map.
4. **Aircraft** — same: approved by ear 2026-09-18, jet balance pinned, not in the game.

---

## The route, for re-testing anything above

```
/tp -20 32 0.3       garage L0     5107 ms   the longest tail on the map
/tp 14.35 -6.7 0.1   stairwell     2061 ms   bright, tile
/tp 20.3 8 0.1       corridor       427 ms
/tp 14.35 3.1 0.1    flat           445 ms   should be nearly dead
/tp 0 -55 0.2        tunnel        3024 ms   dark
/tp 0 -20 0.2        street         525 ms   nearly dry
```

Server: `./run-server.sh city`. Build to tmpfs with `--artifacts-path`, never `dotnet run`.
