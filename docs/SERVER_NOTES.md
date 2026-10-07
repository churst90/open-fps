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
