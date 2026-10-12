---
name: alex-npc
description: 10-06 Alex, a homeless character voice (inbox/npc-voices-2026-09-26/alex, 629 lines, Cody's spec ALEX-2026-10-06.md); agent wiring him in as a named city character
metadata:
  type: project
---

Cody 2026-10-06 added voice "alex" (Qwen3-TTS clone of a real recording, ~/qwentts/gen_alex.py, lines in
~/qwentts/alex_lines.py): homeless, short fuse, profanity intended. 13 homeless_* categories (money,
smokes, weed, drugs, food, ride, shelter, thanks, refused, angry, mutter, cops, cars) + 4 stories +
shared walker lines; NO phone calls, NO driver yells. Behaviour: walks streets, rides the bus, lingers at
bus stops, store fronts and apartment lobbies (lobbies at night/cold). One named character, not
round-robin. Agent started 10-06 (server behaviour, import, gen_city.py). Cloned voice of a real person
in a public repo: Cody 10-06 "yes it's fine, ship it" (see [[pedestrian-speech]]).
**MERGED+pushed 10-06 (db50037e).** CharacterSystem/Haunts/Pavements/PedestrianSpeech.Homeless; 15 haunts found
from the map (2 Main St shelters, 6 entrances, 5 tower lobbies, Market Square, Elm Park); rides City bus 1;
/where alex, /hand; IdentityComponent.Named appended (wire change -> Windows zip + VPS when Cody allows);
OPENFPS_GAME_HOUR / OPENFPS_TIME_MULTIPLIER / OPENFPS_CHARACTER_PACE for testing. Find him: /tp 8 -60 0 (N shelter)
or /tp -8 86 0 by day; Kestrel lobby ~/tp 11 -103 0 at night. Missing: shops, money, cars yielding to him.
