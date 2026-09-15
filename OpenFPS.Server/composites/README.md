# Composites

A composite is a named group of entities that is one thing: a house, a market stall, a barricade, a
vehicle body. Each file here is a template — what it is called, whether it is fixed down, what it is
made of, where people sit in it and what it drives as — with every part positioned in the composite's
OWN frame rather than the world's.

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

## Owning one

Whoever groups or places a composite owns it. Ownership gates four things: taking it apart, saving it
out as your own, changing what it is, and driving it. It gates nothing else — a composite with no
owner is public property, and riding in somebody else's passenger seat is not trespass.

Ownership permits; it never compels. `/enter` with no seat named takes the first seat you are ALLOWED
into, which for an owner is the driver's seat — but `/enter passenger` asks for the passenger seat
and gets it, in your own car as in anybody's. And every refusal names what is free instead, so being
turned away from a seat never costs you a lap of the vehicle to find out why.
