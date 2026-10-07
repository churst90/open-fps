# Server notes

The reasoning behind server code that has no other doc of its own. The code points here.

## Building without pointing

The building verbs are deliberately few: /group gathers what is around you into one thing, /ungroup takes
it apart again, /saveas keeps it so it can be made again, /place puts a saved one down and /savemap writes
the world to disk. A house, a market stall, a barricade and a vehicle body are all the same five verbs.

Nothing in them needs pointing, the one thing a player here cannot do:

- /group takes a radius rather than a selection. "Everything within twelve metres of me" is a selection
  anybody can make, and can widen or narrow until it is right.
- /addseat puts a seat where you stand, facing the way you face: a player can always walk to a spot and
  say "here".
- Walking to the spot works for a wall but not a roof, and walking to twenty wall positions in a row
  does not make a straight wall. So there is a build cursor, moved in metres from an origin the player
  chose (/origin, /at), announcing itself and what is already there each time it moves: a review cursor,
  which screen-reader users have navigated documents with for years. Being told "concrete wall" is how
  you find the wall you placed a minute ago and build the next one against it.
- /put's run is the important half of /put. A wall is a line of parts, and a line placed by hand is only
  as straight as the arithmetic done in somebody's head. A run steps by the part's own footprint along
  the cursor's last direction, so the panels touch and it is one command instead of eight. "run 4 right"
  takes the direction on the run itself; without it the only way to aim a run was to move the cursor
  zero metres in that direction first.
- /undo is not a convenience: a part in the wrong place is invisible to a builder who cannot see it, and
  undetectable until they walk into it, by which time three more things are built round it.
- /room is a dry run of the rule /group will apply, asked before committing. A sighted builder stands
  back and sees the roof is missing; /room is the replacement for standing back.
- /savemap is explicit, never automatic: a world that rewrote its own map file whenever somebody
  experimented could not be experimented with, and the first thing anyone does with a building tool is
  put something in the wrong place.

Who may use these: building, spawning, moving yourself, saving and the sound tools are everybody's on a
map they own, and staff's anywhere (Permissions.OnOwnMap). Getting into things, carrying things, doors
and teams are every player's: building the thing you get into is the part that needs a role. Every
carrying verb takes an optional name, because a player who cannot point has to be able to say which one
they meant, and every refusal says what is in the way rather than merely no.

## Composites

Four operations answer four questions that looked separate: how somebody builds a house, how they
customise it, whether they can later classify it as an object, and whether it is permanent where they
built it.

- Group takes what is already standing there and makes it one thing with an origin. The selection is a
  radius, not a pick: "everything within twelve metres of me" is a selection somebody who cannot point
  can make, and widen or narrow. Anything wider than the sweep (the floor, the field) is left out; at
  thirty metres you plainly do mean the building.
- Ungroup undoes it, leaving the same entities exactly where they were, which makes group and ungroup
  safe to use while experimenting.
- Save writes the thing to disk as a template, in its own frame, so it can be placed again anywhere.
- Place instantiates a template and records the placement in the map's data; /savemap commits it.

Customising is then not a feature: it is grouping, adding or moving parts, and saving again.
"Permanent" is a property of the placement (the map records it), not of the walls.

No new transform system was needed. Members carry a ParentComponent pointing at the root and
ParentSystem, which runs every tick, carries them. A house that never moves and a vehicle you can drive
away are the same structure; only whether anything moves the root differs. The derived room is a part
too, so a house you drive away takes its acoustics with it.

Ownership is not a fence. A composite with no owner is public, an elevated role can do anything, and
nothing gates walking into a building or sitting in a passenger seat: a world where you cannot enter
other people's houses is a street of locked doors.

A composite made drivable gets exactly what the map's own traffic has (a profile, a velocity, a body the
grid can see, an engine voice the client runs from the speed), so all the machinery that makes traffic
audible works on it unchanged. It must be free: a house that drives away is a caravan.

Doors are shut and forget their shut pose when grouped or ungrouped. A door records that pose in the
frame it lives in, and grouping changes the frame: a shed's door opened perfectly until the shed was
grouped, and then its leaf was flung out of the world.
