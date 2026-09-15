# Composites

A composite is a named group of entities that is one thing: a house, a market stall, a barricade, a
vehicle body. Each file here is a template — what it is called, whether it is fixed down, and what it
is made of, with every part positioned in the composite's OWN frame rather than the world's.

They are written by the game, not by hand. Stand among the parts and:

    /group cabin          — make one thing out of everything within 12 m (add a radius to widen it)
    /saveas cabin         — write it here, so anyone can place another
    /place cabin 90       — put one down at your feet, turned 90 degrees
    /savemap              — make what you placed permanent
    /ungroup              — take the nearest one apart again, leaving the parts where they are

`/group cabin 20 free` makes one that is NOT fixed down — the only difference between a house and a
caravan, and deliberately not a difference of kind.
