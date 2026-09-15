# Composites

A composite is a named group of entities that is one thing: a house, a market stall, a barricade, a
vehicle body. Each file here is a template — what it is called, whether it is fixed down, what it is
made of, where people sit in it and what it drives as — with every part positioned in the composite's
OWN frame rather than the world's.

## Putting the parts there in the first place

You cannot point at anything, so there is a CURSOR: moved in metres from an origin you chose, saying
where it is and what is already there every time it moves. A review cursor, on a building site.

    /origin                          — origin at your feet, forward is the way you face
    /at 2 0 4                        — cursor to 2 right, 4 forward: "Cursor 4 forward, 2 right. Empty."
    /at forward 3                    — or step it, relative to where it already is
    /put concrete_wall               — put one there
    /put concrete_wall run 4 right   — run four of them, touching, dead straight
    /put concrete_wall turn 90       — turned ninety degrees from your build heading
    /undo                            — take back the last thing you placed
    /room [radius]                   — stand back and listen to what you have built
    /prefabs                         — what there is to put down, and how big each one is

The axes are yours and they do not move: right, up and forward from where you were standing when you
set the origin. Fixed rather than live, because a coordinate system that turns when you turn is one
where the wall you placed a moment ago has moved.

`run` is the important one. A wall is not one part, it is a line of them, and a line placed by hand
is only as straight as the arithmetic you did in your head. A run steps by the part's own footprint,
so the panels touch and the wall is straight, and the cursor is left at the end of it ready for the
next one.

`/room` is the replacement for standing back and looking. It measures exactly what `/group` would
take, before you commit to grouping it, and it names what is missing rather than just saying no:

    16 part(s) within 9 m. It is 11.2 by 8.2 metres and 4.5 high, and only 3 of its six
    faces are walled — 4 are needed. Open: floor, ceiling, north wall.

They are written by the game, not by hand. Stand among the parts and:

    /group cabin          — make one thing out of everything within 12 m (add a radius to widen it)
    /saveas cabin         — write it here, so anyone can place another
    /place cabin 90       — put one down at your feet, turned 90 degrees
    /savemap              — make what you placed permanent
    /ungroup              — take the nearest one apart again, leaving the parts where they are

`/group cabin 20 free` makes one that is NOT fixed down — the only difference between a house and a
caravan, and deliberately not a difference of kind.

A sweep will not take anything WIDER THAN ITSELF. The floor is within twelve metres of you; it is
within twelve metres of everybody, and you cannot be selecting a thing whose far side is nowhere near
you. Widen the radius and bigger things come into scope, which is also right.

## Getting inside one

    /addseat driver drive  — a seat where you are standing, facing the way you face, and it drives
    /addseat passenger     — and one for somebody else
    /drivable v8_sports    — give it an engine, a gearbox, tyres and a mass. Needs a driving seat
                             first: an engine in something nobody can steer is a shed with an
                             engine in it
    /seats                 — read out what is inside the nearest thing, and which seats are taken
    /enter [seat]          — get in. Or just press E beside it
    /exit                  — get out, onto clear ground beside where you were sitting

Seats and the vehicle profile travel in the template, so a bus placed twice has the same seats in
both, the same way it has the same walls.

Driving: **W** and **S** for throttle and brake, **A** and **D** to steer, **space** for the
handbrake. Back against forward motion brakes; back again once stopped is reverse.

Nothing about how it drives is written in the driving code. What it pulls comes out of its engine's
torque through its own gearbox, what it corners and stops at comes out of its tyres' peak grip
against its mass, and what it will not exceed comes out of its drag area. Give the same shape of car
a lorry's profile and it drives like a lorry. `/drivable` with no argument lists the profiles.

## Doors

    /put door                        — a leaf that swings out of its own doorway
    /put steel_door                  — heavier, slower, and it lets almost nothing through
    /open [name]                     — open the one within reach; from a seat, the one beside you
    /close                           — shut it again
    /doors                           — what is near, which way, how far, and whether it is open

A door is solid the whole time. Opening it moves the leaf aside, which is what a door does — one
that went insubstantial would be a door you could walk through while it was shut in front of you.

What you actually hear is the opening. The aperture on the door's portal follows the leaf, so the
room beyond opens up gradually as it swings, and a door in a building automatically leads out of that
building — which room a doorway joins is a property of where it is, so nobody authors the pair.

A door shuts when the building around it is grouped or taken apart. Where "shut" is depends on
whether the door is loose in the world or part of a building, and grouping changes which.

## The inside of one

A composite that encloses space grows a room, and nobody authors it. Put four walls, a floor and a
roof around yourself and you are indoors — so the question is asked of the geometry:

  * **big enough to be inside** — a fence is half a metre thick whatever its footprint;
  * **mostly empty** — a stack of crates the size of a garage is not a garage;
  * **mostly covered** — four faces of six, so a walled courtyard counts and a pair of walls does not.

What it is made of comes from the walls: each of the six faces takes the material of whichever part
covers most of it, so a glass-sided office is bright and a carpeted one is dead without anyone
saying so. The room is carried like any other part, which means a caravan takes its acoustics with
it and the cab of a car is a room in exactly the way a house is.

If `/group` does not report a room, it did not find one — widen the walls, put a roof on, or check
you have not built something solid.

## Owning one

Whoever groups or places a composite owns it. Ownership gates four things: taking it apart, saving it
out as your own, changing what it is, and driving it. It gates nothing else — a composite with no
owner is public property, and riding in somebody else's passenger seat is not trespass.

Ownership permits; it never compels. `/enter` with no seat named takes the first seat you are ALLOWED
into, which for an owner is the driver's seat — but `/enter passenger` asks for the passenger seat
and gets it, in your own car as in anybody's. And every refusal names what is free instead, so being
turned away from a seat never costs you a lap of the vehicle to find out why.
