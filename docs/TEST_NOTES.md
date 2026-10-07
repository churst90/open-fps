# Test notes

The reasoning and history behind OpenFPS.Tests that has no other doc of its own. The tests point here.

## The silent grandstand

A full stand on the speedway could not be heard (CrowdAudibilityTests). Three things had to be true at
once, and only the last was about the crowd: a transient's range was capped at 250 m and the mixer fades
a voice over the last quarter of its range, so everything past 187 m was fading, and the grandstand is
219 m from the spawn; a crowd was placed as a point with a firework's reference distance while the cars
beside it were placed at their own size; and every reaction rendered a new three-second buffer and
registered a new sound, ninety a minute, because nothing quantised the key. Before the fix the crowd came
out 16 dB under where the inverse square law from each source's own level puts it.

`TwoCrowdsThatDifferByAPersonAreOneSound` is not a claim of a dramatic saving. Its first version counted
distinct keys over a minute of racing and passed whatever the quantiser did, because the intensity is
built from two saturating terms: about fifteen distinct keys a minute with the quantiser, and twenty-two
over the whole (cars nearby, fastest car) grid without it. So it asserts the property: two crowds a person
apart are one buffer, and one twice the size is not.

The reference car in `AStandAcrossTheInfieldIsHeardAgainstTheCars` is one of the speedway's own field at
about the stand's level, because the mix's compression holds relative levels only between sources of like
level. It used to be the sports bike, which passed at 118 dB with open pipes and failed when it got a
stock silencer (107 dB), for reasons that were not the crowd's.

## The stand answers the people on it

"A cheer arriving off the deck a beat after the direct sound is most of what makes a stand sound
occupied" sat in the todo from the crowd's first version, because reflections lived inside the engine echo
system and a crowd is not an engine. The geometry is the same whatever made the sound, so the
image-source search that answers a car answers anything the world reports, and a transient's echo is the
same sound queued again for the moment it arrives (CrowdReflectionTests).

The grandstand was a flat concrete slab, so applause came back as an exact copy at nearly the level of the
direct sound ("the clapping reflections are crisp and they shouldn't be"). Tiered seating with people in it
is the most absorbent and scattering surface in ordinary acoustics; that is a material (Audience), so the
retaining wall beside the track still slaps (ScatteringTests).

## The cabin leak test

VehicleBodyTests' cabin test used to say an open-wheeler has less low end than a saloon. That stopped
being true when the body's modes got their zeros at DC: with the leak at 0.15 a saloon's cabin adds about
as much to the street as the open-wheeler's absent one, which is the model saying what it was built to
say. What the test guards now is the mechanism, that CabinLeak decides how much of the cabin reaches the
street, because that is the number interior audio will turn up.

## Early reflections from the surfaces

EarlyReflectionTests was asked for directly on 2026-09-18, after a session in a map made of walls that
produced no reflections at all: "shouldn't it just be the natural reflections off the surfaces of the
roof walls and such rather than a blanket reverb? the reflections themselves should cause the reverb
naturally right?" and, from outside the same room, "I don't hear the room reflections when I'm outside
the room facing the room coming through the doorway". The tests hold the image-source model to what
makes it a model and not a sound effect: the copy is where the geometry says, arrives when the
distance says, is missing what the material took, and is not there when there is nothing to come off.

## Reflections that did not follow

Cody, 2026-10-04, climbing the stairwell of Marlow Tower: "as I'm walking around the apartment building
I'm not hearing my own reflections follow me, it's like they're left on the first floor", and with
another player walking round him: "I can hear him but it's dry, his reflections don't follow him and
neither do mine". Two faults. Your own steps' copies, each placed at its image behind the surface it came
off and carrying that path, were handed to the occlusion worker as if they were sources standing at
their images: the worker traced each image to your ear through the wall that made it (-40 to -100 dB in
the stairwell's brick and concrete) or round by the openings (out of the ground-floor door and up the
stair openings, heard from the floor below), and that path replaced the copy's own. And another player's
voice had no copies at all: the room it was in answered it only with the late tail.
ReflectionsFollowTests builds two identical storeys, one over the other, so "follows" has a plain
meaning: on the floor above, every copy is where the same copy was on the floor below, one storey up.
