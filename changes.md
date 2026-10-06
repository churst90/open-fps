# Changes

Recent work, newest first. `git log` has the rest.

## 2026-10-05

- The weather's wind makes sound. You hear it at your ears: a low, buffeting rush, louder in the ear
  on the far side of the wind and quieter in the ear it blows straight into, so turning your head
  tells you where it comes from. Facing into it, both ears are the same; with your back to it, both
  a little louder. It is the air moving past your head, so walking into the wind makes it stronger
  and walking with it makes it weaker. Indoors there is none at all, and a walled yard or a street
  between tall buildings takes some of it off. A closed vehicle has none, a car with its windows
  down some, and a motorcycle rider a helmet's worth. It gets no reverb and is not placed anywhere:
  it is in your ears. Walking or running in still air outdoors makes only a faint low rush: the game's
  walk (4.5 metres a second, a jog) is heard as a real walk's 1.4. The levels are what reaches the
  eardrum, which is 14 to 19 dB less than the published wind-tunnel microphone figures.
- The trees, the fire and the fountain now move with the server's weather instead of a fixed
  breeze, and every player hears the same gust reach the same tree at the same moment.
- `/weather` (developers and the administrator) says the weather and the wind. `/weather storm`
  (or clear, rain, snow) and `/weather wind 8 north west gusty` set it for everybody; it arrives in
  a few seconds and holds until `/weather auto`. Try `/weather wind 10 north` in Elm Park
  (`/tp -375 195 0.2`) and turn slowly: the wind moves between your ears, and the trees around you
  roar with the gusts. `/weather wind 0` is still air. Client and server must both be this build:
  the weather message has three new fields.
- The park fountain and the wind in the trees are smoother; their tone and loudness are unchanged.
  The fountain's static came from three things. Only twelve drop impacts were rendered per 3 ms
  block, each standing in for several drops. The falling lumps of water struck as sharply as
  single drops, and each lump carried a hundred times a drop's energy. The bunching of the drops
  stepped the level up and down tens of times a second. Now every impact is rendered, a lump lands
  softly in the foam left by the one before it, and the bunching glides. It is also averaged over
  the eight rim jets and the strands falling off the bowl's lip, since each one bunches on its own.
  For the tree, an eddy hitting a twig was 400 leaf strikes, about ten times what a twig's sixteen
  leaves can make. It is now about 35, so the rustle comes as many small patches instead of a few
  loud ones. Each bough reads the wind where it is in the crown, so a gust takes a second or two to
  cross the tree instead of arriving everywhere at once. The wind field's gustiness goes from 0.3
  to 0.25, the value the surface-layer law gives at a tree's height over this ground. Before and
  after pairs are in inbox/nature-round2-2026-10-05.
- After moving to another map, many sounds played the same in both ears: the fountain stayed in
  front of you however you turned, and about half the speedway's cars had no direction. Each room's
  reverb borrows a binaural stage from the same pool as everything else and sets it to pass its
  stereo through; leaving a map gave those stages back still set that way, and the next sounds to
  borrow them were never placed. A stage taken from the pool is now always fully placed, and a
  room's stage is only pooled again once it has come off its bus. `AudioLab --map-travel` checks
  it: 22 of 40 sources on the right were mono after city to speedway, now none.
- The rooms map (`default.json`) is no longer served. It is a test fixture now, in
  `OpenFPS.Tests/maps`, and `./run-server.sh rooms` is gone. A player last saved on it lands on the
  starting map.
- `/levels real` is gone. 100 percent is still there as a number, but it is literal source levels,
  which on headphones lost footsteps and idling cars; the default, 45 percent, is the setting to use.
  `/levels` now says "the default" when you are on it.
- `/give akm 100` said it gave 100 and made 50 (the most one `/give` makes); it now says 50.
- `/tp` with a bad number said "Usage: /move".
- Roles follow the agreed table (docs/PLAN_2026-10-05.md, docs/SERVER_SECURITY.md). Everybody can
  make maps of their own with `/map new`, make them public or private, and invite people; on your own
  map you can build, spawn, move yourself and save, as developers can anywhere. `/tp` needs a
  teleporter in your inventory, with unlimited uses; it charges for two seconds and is heard leaving
  and arriving (the sounds come with the admin gun work). `/move` is the tool for moving yourself by
  coordinates. Developers can give ordinary items and grant players permissions they have
  themselves; only the administrator gives premium items (the teleporter, vehicles). `/spawn` makes
  walkers, parked vehicles, trains on existing track, and parked aircraft (not flyable yet; no
  jets). Moderators no longer build; developers no longer announce or use `/where`. Old single
  grants of "tp" are dropped: tp is no longer a permission.
- Somebody killed leaves their body where they fell, and you can pick it up: an item with the item
  beacon, called "body of" and their name. E lifts it over your shoulder; it takes both hands, will
  not go on your back, and slows you to 1 metre a second, running or not. `/drop` sets it down. A
  player killed also leaves their things in a bag beside the body ("sean's belongings") and comes
  back after 60 seconds with nothing, told at once and at 10 seconds. Taking the bag takes out what
  fits into your hands, back and pockets; the rest stays in it. Pedestrians are replaced a minute
  later, out of sight. Bodies and bags nobody carries go after half an hour, at most 30 of each on a
  map. A driver shot beside their parked car no longer breaks the car: somebody comes back for it.
- Standing still beside someone no longer moves you. Movement tipped a player's collision body over
  with where they were looking, so a player looking down swept through whoever stood next to them; on
  Kestrel House's roof that pushed Sean into and out of the parapet every other tick until he came
  out of its far side and fell to the ground. People stand upright for movement now, and a push that
  would leave you deeper inside something is not taken.
- Bumping into a person says who: their name, or "someone". People are made of skin, not of the floor
  under them (movement used to write the floor's material into the player).
- The Linux client's log starts fresh each run (the previous one kept as `.prev`). It had stopped at
  1 GB on 2026-10-04 and logged nothing since.
- A test holds the city's 21 vehicles to their approved voices. Checked after "too reverby, all the
  same" (2026-10-05): nothing in the vehicle voices or the reverb has changed since they were approved;
  the commuter cars at a cruise are mostly tyre noise on one shared tyre, 11-20 dB over their engines
  (renders in inbox/vehicles-2026-10-05). Waiting on Cody.
- Other players' footsteps are heard. Each step was worked out from inside that player's own body,
  which counted as a wall of whatever floor they stood on, so every step of every other player came
  through about 8 dB down and muffled. People walking the city were not affected.
- Somebody driving, or riding in, a car no longer makes footsteps along the road: a seated player was
  given the car's speed, which read as running. Client and server must both be updated.
- Vehicle beacons are heard. Every one sounded from inside its own car's floor, so it read as behind
  a wall and was never played. It now sounds from the side of the car facing you, and the car you sit
  in is not counted. Only parked, drivable cars have a beacon; moving traffic is placed by its engine.
- Windows: the client asks for a 1 ms timer. The audio thread ran at 60 Hz instead of 250 and the game
  loop at 63 Hz, so pass-bys stepped in pitch and moving sounds lagged about 110 ms; engine rendering
  workers were slowed by the same tick. The log states the timer resolution at start.
- Doors are heard the first time you open them. Each door sound is a simulation that takes seconds to
  run, and one not ready within a tenth of a second was dropped; the client rendered only some of them
  at start, nothing was kept between sessions, and the towers' glass front doors were rendered at the
  wrong width (1.0 m against 1.9 m) so never matched. Now every sound the city's doors ask for (pushed
  and pulled openings, gentle, normal and hard closes, every width) is rendered at start, each render
  is kept on disk for the build, and the Windows zip ships them all ready (164 sounds, about 30 MB).
  A test checks every door on the city against the list.
- A key unlocking a door no longer sounds like a coin dropped afterwards. The spare keys hanging on the
  ring (two, four or six of them) struck each other as the key went home and rang on through the
  whole unlock; every keyring now has one spare key, as the worn one Cody picked did
  (inbox/door-keys-2026-10-05). Approved by Cody: "excellent".
- The glass front door, the glass shop door and the lift door are physical models now, like the knob,
  push-bar and sliding doors: an aluminium frame round a toughened pane with its seal, latch, bar,
  pull handle, closer and sweep; a lift leaf on its rollers, run by its operator, with the coupler,
  the lock's hook and the rubber edge. A key in a lock is its own model, on any keyed door: the
  keyring jingling, the pins over the cuts, the turn drawing the latch, timed to the door's key
  events. The knob door can be pushed open as well as pulled, and the push-bar door pulled from
  outside by its lever; the approved openings are unchanged. Unheard. Renders and notes in
  inbox/door-models-2026-10-05. Known gaps: the lift's close is soft, the key's turn is thin below
  1 kHz, the glass front door's bar push is about 5 dB over the steel door's, and the client renders
  twenty glass door sounds in the background for about two minutes after it starts.
- The server sends far less: about 1.5 instead of 5.7 megabits a second per player on the city, and
  about 150 instead of 580 packets a second. Things standing still (parked cars, people waiting, a
  bus at its stop) are no longer re-sent thirty times a second; they go again the moment they move,
  and once a second while they wait. Positions are sent in a tighter form that keeps them to the
  millimetre. Your health and what is under your feet are sent only when they change. Nothing should
  sound different. Client and server must both be updated together. The next steps, which trade
  something, are in docs/PLAN_2026-10-05.md.
- Doors open the way real ones do. Every hinged door is pushed from one side and pulled from the
  other: room doors swing into the room, and exit doors (push-bar doors, the towers' front entrances,
  the roof and terminal doors) swing out. A push bar is only on the inside. From the street a tower's
  front door is locked: the key goes in and turns, then you pull the door open. From inside you push
  the bar. No door moves into a person, whatever moves it: a closer waits, an automatic door reopens,
  and you cannot shut a door on someone ("Someone is in the way of the door"). A door you push open
  into someone stops against them. The key's own sounds are coming with the door models.
- Guns have a fire selector: X moves it on, Shift+X back. The AKM and AR-15 have safe, semi and
  auto; the 1911, pump shotgun and M700 have a safety; the Glock and revolver have none. On safe the
  trigger does nothing. On auto the gun fires for as long as Enter is held.
- The admin gun (administrator only) never runs dry and fires any calibre (Y, or `/calibre`). X sets
  what it does to what it hits: kill, vaporize, freeze for 10 seconds, or inspect (says what it is,
  whose, its id). Vaporized things come back on restart unless the map is saved; floors and ground
  cannot be vaporized. Its report is the calibre's own with a marker sound; `/admingun report 1-5`
  tries the five candidates (renders: inbox/admin-gun-2026-10-05). Unheard.
- The teleporter is heard: a rising charge on the person holding it, a low thump where they left (the
  air rushing into the space), a pop where they arrive. Something given plays its own hand-over sound
  at the receiver with the spoken line: a gun's action, a sword drawn a little, the teleporter's
  ready tone. Unheard.
- Six fixes from the Resonance port (unheard): tyres, their squeal and the rolling roar were 9 % sharp
  on a 48 kHz device; the city bus's air compressor knocks at the back with its engine; trains read
  from a model file keep their third axle; a machine built on another keeps everything its base had
  (the side-pipe V8's pipe is on its right side); an air horn written as a model reaches buses and
  trucks; a bell's blows render with their own headroom instead of being flattened.
- Doors are heard again. Every door's sound was placed on the edge of its leaf, which overlaps the
  wall by 5 cm, so it was inside the brick and inside the leaf: a front door from 3 m away in plain
  view came through at -46 dB. Door sounds now come from the handle (7 cm in from the edge), 25 cm off
  the leaf on whichever side you are on: 0 dB from the same spot.
- A sliding door's sound moves with its handle across the doorway as the leaf slides, from where it
  starts to where it stops. It used to play the whole run from the middle of the glass where it
  started.
- Each house's patio door is joined to its back garden; it was joined to "outdoors", which is not where
  it opens.
- Water, fire and wind in trees, as physical models (unheard). Nothing is a recording.
  - Water: drops striking a pool and the bubbles they trap, each ringing at the note its size gives
    it (3.26 / radius), rising as it goes; coherent water plunging and making bubbles of every size.
    Drop sizes, speeds and which drops trap a bubble follow Medwin (1992) and Atlas (1973). Fitted
    to a recording of a fountain (octaves 500 Hz-16 kHz within 2 dB) and to Watts (2009) for level.
  - Fire: flames that roar and puff at 1.5 / √width per second; crackles from steam and resin pockets
    bursting, sizes on a power law measured from recordings, in clusters; big ones throw an ember that
    ticks on the brick; steam hissing and sometimes whistling from log ends; a log settling every
    minute or two with a rattle of charcoal and a flare. Its crackles stand 45 dB over its mean, and
    the voice renders with that much room, so they are not clipped.
  - Wind in trees: leaves on twigs flutter and strike each other in short episodes, boughs sway on
    their own frequencies, and air sheds vortices off the twigs (a pine sighs rather than rustles).
    The level rises about 10 dB when the wind doubles, as Fégeant (1999) measured.
  - One wind field for the whole map: gusts travel downwind at the wind speed, so a gust reaches the
    trees upwind of you first. The fire roars harder and the fountain's spray drifts in a gust.
- Elm Park, north of the housing estate (the open ground inside the rail loop): a lawn with paths, a
  tiered fountain in a square basin in the middle, and fourteen trees. A path leads in from the north
  end of Sycamore Lane.
- 58 Alder Street's back garden is fenced in timber with a gate onto the strip before the park, and
  has a brick fire pit with a fire burning.
- AudioLab `--nature`: levels, renders and texture statistics for each model; `compare=FILE.wav` for
  a recording; `live` plays each through the real FMOD voice path and checks its level against the
  loudness law.

## 2026-10-04

- Your gunshots come from you where you are when they are heard, not from where the server had you,
  which while walking was a step behind.
- A driver's yell stays at the car's window and moves with the car; a pedestrian's line stays at
  their mouth as they walk. Both used to trail behind by however far they had moved before the line
  was heard.
- The server logs voice chat per sender every ten seconds: frames, bitrate, map and how many players
  it went to.
- I opens your inventory as a list: everything in your hands and on your back, one entry each with
  its rounds ("AKM, 30 rounds, on your back"); Enter offers take in your hands, sling on your back or
  drop, for that one thing even among ten of the same name. Shift+I says it all in one sentence.
- A thing you put down is an item beacon again. Things you carry no longer count as item beacons:
  they were always the nearest, and took every item beacon's place.
- E picks up an item lying within 2 metres (before shutting a door). It used to do nothing.
- Dropping something where no floor was found sent it 1000 metres under the map; it lands at your feet.
- Stairs and roofs. The stairs in the city's blocks of flats could not be climbed: the slabs were laid
  whole over the stairwell and each flight stood under the next, so you stopped at the second step.
  Each stairwell is now a dog-leg: go up a flight, turn round, and the next flight starts beside you.
  Every block of flats has a last flight up to a stair housing on the roof, with a steel push-bar door
  out; the roof is a named place ("Marlow Tower roof") with a waist-high parapet all round. Each end
  of every flight plays the stairs beacon (four rising notes), and reaching it facing along the flight
  you are told, once, "Stairs up, 10 steps, to floor 3", "Stairs down, 10 steps, to floor 2" or
  "Stairs up, 10 steps, to the roof". The floor's name is said when you step off the stairs, not
  halfway up. Footsteps going up are lighter and a little higher, going down heavier and a little
  lower, and on the stairs each lands on a tread. AudioLab `--walk` takes a route (`via=x,z;x,z`)
  and `trace`.
- After the hit or kill chime you are told what the shot struck and how far: "Hit pedestrian at 17
  metres.", "Killed sean at 40 metres.", "Hit sean in the head at 340 metres." Surfaces the same way:
  "Hit concrete at 17 metres."
- A rifle scope. The M700, a .308 bolt-action rifle with a 4-12 power scope (staff: /give m700).
  Numpad star raises it; Num Lock must be on, and the game says so if it is off. Numpad 8, 2, 4 and 6
  aim in small steps that get smaller as you zoom in; 5 says what is on the crosshair; 7 and 9 read
  out the people and vehicles in view; plus and minus zoom; 1 and 3 set the elevation turret and say
  the distance it is zeroed for; period is the rangefinder; hold 0 to hold your breath; slash or
  Enter fires. A soft tone pulses faster and higher as the crosshair nears someone and holds a note
  on them. Shots through the scope fly: they drop, the wind carries them and they take time to
  arrive (about a second to 600 m), so aim high for distance and ahead of anyone moving. A miss on
  your target is called out ("40 centimetres low"). A head shot does double damage. The bolt is
  heard after every shot. Renders in inbox/scope-2026-10-04.
- Gun handling recordings with the room taken out: inbox/weapons-dry (tools/dereverb_wpe.py).

## 2026-10-03

- Car windows roll down and up: R in a vehicle, or /window, /window down, up or half. Anyone seated
  can do it and it moves every side window: about 2.5 s down and a little under 3 s up. The sound is
  a simulated power window (motor, worm gear, the glass in its rubber channels, the seal at the top
  and the stop at the bottom), each door's a little different, heard inside and quietly outside
  (inbox/car-window-2026-10-03). With the windows down the street, the wind and your own engine come
  in. Somebody on voice chat in a car is heard through the car: muffled from outside with the
  windows shut, clearly with them down, so you can hear them as they drive by. Parked cars start
  with their windows up.

- Guns hold real magazines: AKM 30, AR-15 30, Glock 17, 1911 7, .357 revolver 6, pump shotgun 6
  shells. Each shot spends a round. An empty gun clicks and says "Empty. R to reload."
- /reload takes as long as the hands take, and you cannot fire meanwhile. A reload from empty takes
  longer, because the bolt or slide must be sent home. A pump gun is loaded one shell at a time.
  Everyone near hears a reload and an empty click; the sounds are synthesised, fitted to recordings
  of real guns (inbox/reload-sounds-2026-10-03).
- Spare rounds are kept per calibre. A gun you pick up or are given for the first time brings three
  spare magazines. /ammo says what is in your gun and what you carry; /draw, /stow and /inv say
  the rounds. Staff can give ammunition: /give sean 9mm 60.
- Shooting a person takes health. Buckshot spreads with distance; pistol rounds weaken past 50 m.
  The shooter alone hears a chime for a hit and a rising three-note figure for a kill.
- A killed player is told "You died." and gets up at the spawn after 5 seconds. A player who is hit
  is told how much health is left. People in the street can be shot: the body falls, which everyone
  near hears, is taken away after 10 seconds, and somebody else walks the same route.
- R depends on what you are doing: in a vehicle it works the window, with a gun it reloads,
  otherwise it slings what you hold. Enter fires only with a gun in your hands; otherwise it
  interacts, like E.

- Players are beacons. Every other player near you calls with two soft notes falling a minor third,
  G4 then E4, from head height: the nearest four within 30 metres, never you. A player in your team
  calls the same notes on another instrument: hollow and a little reedy (the login chime's octave and a
  triangle's odd harmonics), the same pitch and as loud. `/beacons player` switches them; a map can force
  or forbid them like other beacons.
- Teams: `/team create`, `invite`, `join`, `leave`, `list`, `kick`, `open` and `close`, and `/t` to
  talk to your team. Up to 16 players, kept in teams.json beside friends.json. When someone joins or
  leaves, their beacon changes tone for everyone nearby straight away. Team chat has its own sound
  and is read in the Private ring.

- Presence notices. When someone logs in, logs out, loses connection, is kicked, or goes away,
  everyone else gets a line on the All channel: "cody is online.", "cody logged out.", "cody lost
  connection.", "cody was removed from the server.", "cody is away.", "cody is back." Away is /afk
  or five minutes with nothing done; back is the next thing they do. Logging in again from another
  machine announces nothing. Each notice has its own sound, chords on a triangle wave that nothing
  else uses: C major rolled upward for online, the same rolled downward for logged out, a C minor
  stopped short and two broken stabs for lost connection, A minor sinking into E minor for away and
  the reverse for back. Settings has "Online and offline sounds" to turn the sounds off; the notices are
  still read. AudioLab `--presence-sounds` writes the five sounds (inbox/presence-sounds-2026-10-03).

- Roles and permissions. A fourth role, Moderator: announcements, finding, going to and bringing
  players, kick and mute; no building. Each gated command is a permission; roles are sets of them;
  an Admin can give one account single commands (`/grant NAME COMMAND`, `/revoke`, `/perms`).
  New commands: `/tp NAME` (go to a player), `/bring`, `/move NAME x y z` and `/move NAME to OTHER`
  (Admin), `/kick`, `/mute`, `/unmute`, `/give [NAME] ITEM`. Table in docs/SERVER_SECURITY.md.
  The accounts database gains Permissions and CustomRole columns on first start (backed up first).
- Custom roles: an Admin makes a named role with its own permissions (`/role create builder tp
  spawn`) and gives it with `/setrole`. The player is told "cody made you a builder. You can now
  use: spawn, tp." Grants say who gave them. `/give [NAME] ITEM [COUNT]` says "You gave sean 1
  AKM." and sean hears "cody gave you 1 AKM." and where it went.
- Saved Servers (Linux): Tab leaves the list instead of choosing the next server.
- The command box: `/help` lists the commands you may use, by group; `/help COMMAND` says how to type
  it and what it does; `/help settings` lists your own sound settings. A mistyped command is answered
  with the nearest one you can use ("Did you mean /give?"). A player can be named by the start of
  their name when only one fits, and an item by its name or the start of it (`/give sean akm`).

- Connecting to a server by name (codyhurst.com) uses its IPv4 address. LiteNetLib picked the IPv6
  address, and that connection was never answered; by IPv4 the same login is accepted.
- The VPS runs build 074d2a719a38 (voice chat). The previous build is backed up on the VPS as
  `~/openfps-server-backup-e27b577838ac.tar.gz`.

- Bursts of street noise when walking between rooms are fixed. A doorway is the wall's thickness and
  lies in no room's box, so for sound it was treated as outdoors: in the Brandt Court stairwell's
  doorway the outdoor reverb went from 1 % to 100 % while Cody stood in it, carrying the traffic and
  sirens. A point in a doorway between two rooms is now in the room on its side; a doorway to the
  street is still half in and half out. Zone names still say "doorway between A and B". Unheard.

- Voice chat between players works end to end and is heard in the world. What you say goes to the
  server and from there to everyone on your map; each listener hears you from your mouth, at a
  person's speaking level, through the walls and openings between you and in the room you are in.
  - Codec: Opus at 64 kbit/s, full band (to 20 kHz), highest complexity, with in-band error
    correction. It was 24 kbit/s VOIP. Both clients use the same settings (`VoiceCodec`).
  - A microphone not at 48 kHz is resampled with a windowed-sinc filter (`SincResampler`); it was a
    straight line between samples.
  - Every packet carries a sequence number (`VoiceData.Sequence`, appended). Old clients send none
    and their packets are played as they come.
  - Each listener keeps one continuous voice per talker (`TalkerStream`) instead of a new sound per
    20 ms packet. Packets are put back in order; a missing one is rebuilt from the next packet's
    error-correction data or concealed by the decoder. The jitter buffer sizes itself to the
    connection: 70 ms at least, 500 ms at most. It grows by waiting and starts each new run of
    talking at the full buffer; the read rate is never moved by more than 0.2 %.
  - The server relays voice the moment it arrives instead of at the next tick, and both ends send
    it at once instead of at LiteNetLib's next 15 ms update. On the loopback the median delay from
    sender to listener went from 19.5 ms to 2–3 ms.
  - Measured on a live server with two clients: 150 of 150 packets, in order, none lost; each
    harmonic of the test voice within 1 dB of what was sent; no buffer underruns.
  - The wire contract changed: the Windows build and the VPS server must be updated together.
  - Unheard.

- Your own voice in the room you are in (Cody: "I want to hear myself in the room I'm actually in").
  While the microphone is open (V), what it hears goes straight into your room, not round the
  server: up to eight surfaces answer it, each from its own direction after its own path (found as
  your footsteps' echoes are, from your mouth to your ears), and the room's reverberation is fed
  from you. Your voice itself is never played. Its level is your voice's: your usual talking level
  on the microphone is taken as normal conversation, so a shout fills the room more. A surface
  more than about ten metres of path away answers at its true time; nearer ones as soon as the
  microphone's own 60 ms allows. V off now has its own cue. (`OwnVoiceRing`, `OwnVoiceTap`,
  `ClientAudioSystem.UpdateOwnVoice`; unheard.)

- Both clients: **Create account** is on the main menu. It opens a blank form for the preferred server
  with Create account first (it never uses the saved account there); a new account is saved as its own
  entry for that server, and the preferred one stays as it was. A refused account or login now drops
  the connection: it stayed up, and quitting announced "Disconnected from the server". The Create
  Account form has only Create account and Cancel, says the 8-character password rule, and on a
  refusal puts the cursor on the status line (on Username, Orca read the field over the reason). **Open log folder** moved from the main menu
  into Settings (Cody: "viewing the log should be an option in the settings dialog").

- Patio and automatic sliding doors are physical models in the game (`OpenFPS.Common/SlidingDoor.cs`,
  lab `--sliding-door`), round 2, the one Cody chose ("much better"; round 3 was too gritty and thin).
  - Patio: four 1-1/4 in nylon wheels on a sill rail riding its roughness, their flats and grit; pile
    drag; the hook latch; the stile meeting the jamb and the leaf arriving through it; the handle
    knocking in its play; the hand holding the leaf home while the latch is thrown.
  - Automatic: polyurethane carriage wheels in a header, a brushed motor and two-start worm to a
    toothed belt with play at its clamp, an S-curve controller at 0.7 m/s open and 0.3 m/s shut.
  - The server sends each run as it starts (`motor-start`, patio `latch-retract` and closing
    `rollers`), and moves an automatic leaf in its controller's own time for its width.
- The client prewarmed the push-bar door through the knob door's renderer, which gave sixteen samples
  of silence: prewarmed push-bar doors were silent. Each key now goes to its own model.
- `inbox/doors-approved-2026-10-03/`: every approved door side by side, dry.

- Doors rebuilt after Cody's round-3 verdict ("too quiet ... abbreviated ... too tonal and
  synthetic ... the door and the latch are too close together ... push bar is usually hollow") and
  a research round (reports/Realistic door sound synthesis.md, notes in research_notes/).
  - Acceptance checks against the recordings, before anything is played to Cody: spectral flatness
    of the hit, latch events ahead of it, a settle train after it, ring length, level.
  - Dense fields (`DoorPhysics.DenseField`): a thin panel's modes at the density its geometry
    gives (a mode every 1.7 Hz for 1.2 mm steel), stood in for by effective modes 40 Hz apart that
    carry the energy of the modes they stand for, radiating by Maidanik's efficiency, with loss by
    material (thin panel, sandwich, wood). Fed through `Port`s (a patch of panel on its backing,
    passing energy in through the panel's point impedance), so a field never takes more than the
    blow gives: fed one way, a thin case had radiated more than the pad that struck it.
  - Acceleration noise for small hard parts (bolt, strike, latch body, pad, drive bar, knobs).
  - Knob door: the hand closes at a real speed at the strike (gentle 0.08, normal 0.22, hard 0.4
    m/s, slam thrown at 1.2), guided in to the latch, so the bevel's touch comes 50-150 ms before
    the stop. The knobs rock in their play (the jiggle). The strike plate's give feeds back into the
    bevel contact (re-strikes). The bevel scrapes on the lip's worn track. The game picks gentle,
    normal or hard for each close and sends it when the leaf arrives.
  - Push bar: the device is a hollow box (aluminium case and pad as dense fields, the air inside at
    its length and cross modes), a drive bar with its own stops behind the pad, a 3/4 in latch, a
    stroke of about 50 ms, the bar let go a third of a second after the bolt clears. The closer
    brings the door in at its latch-zone speed (0.07-0.6 m/s by character), so a well-set closer
    scrapes the bevel for half a second before the latch drops. In the game: the steel door sends
    `pushbardoor:open|close`; the client pre-renders its four characters.
  - Levels: one calibration for the door model (14 dB) puts it on the measured ranges: knob normal
    close 77.5 dB at a metre, gentle 63, hard 94, opening 68; push bar opening 88-92, closer close
    67-100 by character.
  - Still failing the checks dry: knob normal and hard closes, and three of the push bar's closes,
    are less noise-like than the recordings above 4 kHz.

- Knob doors are a physical model (branch knob-door, `OpenFPS.Common/KnobDoor.cs`). Cody,
  2026-10-03, after round 2: "I'd go with these sounds for these types of doors for now", with
  the opening loud enough to hear down a corridor of flats.
  - What is simulated: the leaf as a plate on its hinges (bending modes and their radiation from
    its build: hollow core or solid wood); three hinges whose pins stick and slip when dry; a sprung
    bevelled latch, the strike plate and its keeper; the stop moulding, struck at five points of a
    leaf that is not flat; the stud and plasterboard of the frame; the knob on its cam and rose. A
    hand turns the knob, pulls (12 N), swings the door and lets go.
  - Round 1 faults Cody heard and what they were: the opening "like a clinking wine glass" was the
    knob modelled as an open brass cylinder (a knob is a closed ball on a base disc; it rings near
    10 kHz, briefly); "plastic" was leaf and frame losses twice wood's. The slam still sounds small;
    a solid door slams bigger.
  - In the game: the server sends `knobdoor:open|close:...` for knob (Hinged) doors, the opening on
    the latch letting go and the whole close as the leaf starts back, so the frame is met when the
    leaf arrives. A door's id picks one of four characters; one in four has worn hinges. The client
    renders each in a few seconds of a core, so it renders the city's door sizes (0.9, 1.1, 1.4 m)
    at start, four at a time, before anyone opens one.
  - Levels: opening 71 dB and closing 104 dB at a metre are declared. That is the model's own
    A-weighted level less 12 dB (`LevelCalibrationDb`), because it radiates about 2.5 % of the
    leaf's energy where real impacts manage a tenth of that. `KnobDoorTests` renders and checks them.
  - Lab: `--knob-door [only=] [stems=DIR] [pins]`. Listening sets: inbox/knob-door-2026-10-03 and
    -round2.
  - Not heard in the game yet. A door closed from part open, or of another size, renders when first
    heard and so is silent that first time.
- Client fixes, so the Linux and Windows clients do the same things (branch client-parity).
  Connecting, logging in, creating an account, reconnecting, the game menu and logging out moved
  out of the two heads into the shared session (`ClientGameSession.Connection.cs`); each head now
  only shows the windows it is asked for.
  - No spoken prefixes: F says the direction, H the percent, Z the area. The server's lines (message
    of the day, arrived and left) have no "Server:" in front; their sound marks them. Command
    replies lost "Reverb:", "Levels:", "Echoes:", "Cabin:", "Beacons:", "Valve flow:" and "On your
    back:". The "Interaction '...' received." placeholder is gone; out of reach says "Too far away."
  - A dropped connection is said, every sound in the world stops, and the client logs back in
    every 3 seconds with a quiet tick. It says "Reconnected" and loads the map again, or after a
    minute says it could not and goes back to the main menu. A rejected login while reconnecting
    (a new build on the server) goes back at once.
  - Escape opens the game menu: Keep playing, Main menu, Quit, with Keep playing focused. Main menu
    and Quit send a logout, which the server now handles (it disconnects the peer at once instead
    of at the timeout), and fade the world out over half a second. Closing the window also tells
    the server.
  - Loading: a soft note at each tenth of the map load, rising an octave. The world fades in over a
    second at arrival; interface sounds are not faded (they have their own FMOD group). The GTK
    loading window has a progress bar like the Windows one.
  - The server sends entity definitions 256 to a message (`EntityDefinitionBatch`) instead of one
    each. The city loads in 1.7 s over the loopback.
  - The GTK client has Create account and Open log folder, as the Windows client does.
  - Shift+R draws the first thing on your back. The key help in both game windows is one shared
    text.
  - Linux voice chat: the GTK client records through FMOD on the microphone chosen in Settings and
    sends the same 48 kHz Opus packets as Windows. `--mic` in the lab checks the device opens.
  - OpenFPS.Common changed (a new message), so the build hash changed: the Windows zip and the VPS
    server both need updating, and the server must be restarted.

- Nothing that is not a number gets into the mix (branch early-tail). Cody, in Marlow flat 00B
  through its door: "a pop ... and the audio just cut out". From that moment the master's loudness
  meter read NaN: one NaN had reached the master limiter, which then holds it for good.
  - Not reproduced. The lab walked the same path over the city with the door opening, through
    the whole mixer (`--nan-mix`), and traced from the walk, inside the door leaf and on its faces
    (`--nan-walk`, `inside`). Every trace, every late-field answer and every built tail was finite.
    So were degenerate traces (empty, all zero, shorter than the tail). The early-tail code
    (`EarlyCopies`, the start at the first reflection, the 192-sample wait) produced none.
  - Found: Steam Audio's binaural effect puts out NaN for a zero, a 1e-20 or a NaN direction,
    and keeps it in its state (`--early-tail hrtf`). Every binaural call now takes its direction
    through `Phonon.SafeDirection`.
  - Found: the averaged tail (`SmoothTail`) is recursive. A trace with one NaN would have stayed in
    it until the next fresh start, and every tail built from it would be NaN. Such a trace is now
    left out. Late-field answers that are not finite are ignored; a NaN ratio used to pass the
    `<= 0` test and become the send.
  - Every custom DSP that writes into the mix now checks its block (`NonFinite`). A block with NaN
    or infinity becomes silence, the unit forgets its state where it can, and the log names it once:
    `[NONFINITE] <unit> <sound>, region <id>`. The units: the traced reverb stage (its input too), a
    voice's binaural stage (and what feeds it), engine voices, taps and echoes, machine, synth and
    granular voices, the ambisonic bed, the traced echoes, and the master's boundary stage, which
    also zeroes a bad block of the whole mix before the limiter. So the limiter never sees one and
    the game comes back on the next good block. No allocation or lock on the mixer thread.
  - Main has all of this except the early-tail commit, so main could do it too.

- The room answers from its first reflection (branch early-tail, unheard). Cody: "a delay between
  when I clap and when I hear the reflections". The traced response started at 50 ms, faded in to
  100. Before that there were only the placed copies: a clap in flat 01F had them at 6-26 ms and
  50 ms and next to nothing between (2 ms steps down to -54 dB at 46 ms with a click). Three changes:
  - The traced response now starts where the nearest surface answers (the ceiling, 5.9 ms in the
    flat). The copies' energy is taken out of it, band by band, from the frames the trace put it in
    (`EarlyCopies`). Steam Audio's response has no peaks to cut: it is noise in 10 ms bins
    (`--early-tail`). Copies plus what is left is what the trace had.
  - The copies a room places are worked out in one place (`WorldAudioPlayer.PlanRoomEchoes`), for
    the clap and for the trace.
  - The reverb bus was 4.4 ms early. Steam Audio's binaural effect delays a sound 289 samples at
    the voices' 1,024 and 97 at the traced stage's 256. The stage's input now waits the difference
    (192 samples). A click 30 ms into the response landed 25.5 ms after the dry click; now 30.0.
  - `--clap-room` now plays the copies, the washes and the clap's floor bounce as the game does, and
    prints the energy in 2 ms steps, C50, C80 and D50. `early=old` gives the old response,
    `copies=0` leaves the copies out, `probe=MS` measures the bus's timing.
  - Flat 01F, click, 8 claps: the deepest 2 ms step between the first reflection and 80 ms was
    -54.3 dB (46-48 ms); now -40.6 dB (20-22 ms). The level from 100 ms on is the same within 1 dB.
    50-300 ms is 2-3 dB up: the old fade took the room's own energy out of 50-100 ms. EDT from 50 ms
    is shorter (click, 2 kHz: 1.10 to 0.83 s): the fade made the decay start late. T20 within
    0.07 s. IACC 300-900 ms unchanged.
  - Not yet: own footsteps get copies but no washes, so the trace gives up a little more than their
    copies carry. Speech and steady sounds get no copies at all, so they lose that share of the
    first 20 ms.

- Paths that popped for a third of a second (branch path-pops, unheard). The [POP] lines from the
  Main Street pavement were cars 150-300 m away going from -80 dB to -20 in the mid band and back.
  New lab `--pop-hunt ear=x,y,z`: 40 cars drive the city's streets, asked about at the game's
  cadence through the real worker, and every answer watched as `[POP]` watches a voice (mid and
  high band). Three causes, each fixed:
  - The barrier search believed a route over or round a box only if it touched nothing else. Over
    the park's 1.1 m wall is a few millimetres, but each pier on the wall, and each storey of a
    building, threw the route out, and the answer fell to what comes through every wall on the line
    (-61 to -100). Now a route that runs into another box bends round it too (up to 2 boxes), and the
    way over the top of everything is always tried: the string pulled tight over every box in the
    vertical plane, as ISO 9613-2 and CNOSSOS-EU draw it. It does not exist from under a roof. A route
    round a door leaf is not bent on (that is the openings' job): it found the 5 cm gap over Selby
    House's shut glass door. A blocked line that only grazes is 5 dB (Maekawa), not 0.
  - A full source pool sent the same sources to the hand-rolled tracer every tick, for good: with 80
    asked about and 64 places, the last 16 in line never got one. That model put a car at -15 where
    the simulator said -63. Now whoever was refused goes first next tick, and keeps its last answer.
  - An engine that won a voice with no worker answer yet started unoccluded, at full level, and was
    pulled down a fifth of a second later. [POP] never saw it (it skips a voice's first half second).
    It now starts on the answer for a source near it, or the one-shots' path.
  - Main Street pavement, 40 s: 58 mid-band excursions before, 0 after. With 30 more sources on the
    pool, 60 s: 21 before (up to 73 dB), 2 after (up to 19 dB: a pier on a wall, where the string
    goes over the pier). Selby stairwell 28 to 7, Marlow corridor 7 to 5; the ones left peak at -49
    to -62 dB, high band below -80 (see todo).
  - Cars behind buildings are louder now: over the roofs at Maekawa's 24 dB ceiling plus spreading,
    where they were 60-100 dB down through every wall.
  - Route queries cost more where legs are blocked: 630-950 us each in the stairwell and the
    corridor, was 250-470. The 4 ms route budget per tick still holds.

- The late tail no longer rings (branch tail-ring, unheard). At the ear, 400-900 ms, with
  `--tail-steady`: 10.4-10.7 % of bins 10 dB over their local median and a flatness of 0.18 in the
  stairwell, the flat and the corridor; now 0.08-0.12 % and 0.52-0.55 (noise: 0.1 % and 0.56). The
  late part was one noise spread over twenty directions by velvet filters, then a velvet filter per
  ear: three random spectra multiplied. Now each direction has its own noise under the averaged
  energy (`DiffuseLate`), straight through its head response. No velvet and no ear velvet.
  - Cost. The late part starts at 250 ms, so it is convolved in 4,096-sample blocks; each block's
    answer is worked out over the next block's mixer pieces and waits in a ring. The noise is made
    once (25 MB). Each trace gives only the envelope: per block, the amplitude at its start and end
    per frequency bin. `--tail-cost` (Release, one thread): 229 us a piece, the old way 242 us.
    The shared FFT is twice as fast (each stage's twiddles in a row, eight at a time).
  - Level. The old late part was 4-6 dB low from 125 to 500 Hz at the ears (`--tail-iacc`): its
    120 Hz split and its 400 Hz ear split each added their halves out of phase, and the velvet's own
    low end. The field is within 0.8 dB of flat (the head's own response). So the late tail is
    fuller below 500 Hz. Clap in the flat: levels the same to 0.1 dB; T20 +15 % at 125 Hz, +10 % at
    500 Hz, the rest within 6 %.
  - No split at 120 Hz for the room you are in: the head makes the low end alike at the two ears
    (0.91 at 125 Hz), not identical.
  - New lab: `--tail-cost`; `--tail-steady late=velvet` and `--clap-room late=velvet` for the A/B;
    `--tail-iacc` prints level and IACC per octave for both ways; `--clap-room` prints the late
    part's IACC.

## 2026-10-02

- Door types (Cody's list): a knob or lever, a steel push-bar door with a closer, a keyed glass
  front door (bar inside, key outside, closer), a pulled glass door with a closer, an automatic
  sliding door, a patio slider and a lift's doors. The kind is data on the prefab (`DoorKind`,
  `Slides`, `Powered`, `SensorMetres`, `CloseAfterSeconds`, `CloseSeconds`, `KeyedSide`) and on
  `DoorComponent` (fields appended; restart the server).
  - Sliding leaves move along their own width; the opening follows, and the routes see the leaf
    where it is (a shut slider blocks like a shut door).
  - Closers shut 3 s after the doorway is clear, slowly and then at latch speed, and never on
    anyone in the doorway. Automatic doors open for anyone within 2.5 m, either side, close 2 s
    after, and reverse for anyone in the doorway. Parked drivers leave both to themselves.
  - Each kind sends its mechanical events as `door:KIND:EVENT`, for now with the existing door
    sounds or none. Every key is in docs/DOOR_TYPES_EVENTS.md. Synthesis follows.
  - The city: knobs on the flats and house fronts (397), a keyed glass front door on each tower
    (5), two pairs of automatic leaves at the terminal's apron entrances (4), a patio door onto
    each back garden (64), push bars on the hangar and terminal service doors (2).
  - Closing a door by E, and a parked driver checking for a player, measured to the doorway in
    the building's own frame; both used to compare a world position with a building-local one.
- The room's tail holds still while you do (unheard). The listener's trace is redone every 250 ms;
  its omnidirectional channel is the same each time, but its directions are a Monte Carlo estimate
  and the directional part (50-350 ms) was re-split every trace. At the ears a steady hum's
  harmonics swung 2.4-3.9 dB from one 54 ms window to the next above 500 Hz (a held response: 0).
  Now the tail is measured as energy per octave and per 5.8 ms frame, and its directions per
  direction over six time cells and two band groups; these are averaged over traces (a quarter per
  trace standing still, more past half a metre, a fresh start past a metre, in a new region, or 6 dB
  off); and the tail is played through fixed noise per band and per direction under that energy
  (`SmoothTail`). Below 355 Hz the directional part comes evenly from all twenty directions.
  - Stairwell, hum swing per window, raw to smooth: 1.3/2.9/3.9 dB to 0.4/1.0/1.0 (100-500 Hz,
    0.5-1.5 kHz, 1.5-4 kHz). Flat 01F: 0.8/2.9/3.6 to 0.4/0.7/0.9. The ear moved up to 30 cm at random
    each trace: 6.0/5.3/5.3 to 0.9/1.2/1.7. Steady noise level and left-right spread: as a held response.
  - `--clap-room`, raw to smooth: level 50-300 ms the same (within 0.2 dB); T20 within 7 % at every
    octave; EDT within 9 % from 250 Hz up, 18 % longer at 125 Hz; IACC per band within 0.06.
  - Costs 52-59 ms per trace on the tracer thread (the first 0.7 s, making the noise), 14 MB.
  - `--tail-steady [room=stair|flat|corridor] [jitter=CM]` measures all of this, raw against smooth.
    `--clap-room tail=raw` plays the old tail for the A/B.
- The metallic ring is not in the trace (raw or smooth, the tail's spectrum is noise: 0.05 % of bins
  10 dB over their neighbours). It is in the late renderer: at the ear, 400-900 ms, 11 % of bins
  stand 10 dB over (21 dB at the 99.9th percentile), where the directional part, 60-240 ms, has
  0.1 %. See todo, acoustics.
- The ear overloads: a sound louder at the ear than the output can play makes every other voice
  give way by the excess (`Loudness.OverloadDb`), held 50 ms and recovering over up to a second. The
  shot, its echoes and the reverb are left alone. Measured with `--clap-room sound=glock dist=N bed`:
  16 dB at 0.5 m, 9 at 10 m, 5 at 30 m, 2 at 100 m. Logged as `[OVERLOAD]`.
- Gunshots refitted to the NIJ recordings: a band-passed gas outflow (critically damped it is the
  Friedlander pulse), the lab's positive phases, energy normalisation, and a 9 % speed error gone
  (rendered at 44.1 kHz, played at 48). Band error against each gun's own takes: Glock 8.1 to 1.6
  dB, .45 6.7 to 1.9, AR-15 6.6 to 1.4, AK 4.4 to 1.9. A .357 revolver (`revolver357`, 164 dB, a
  cylinder-gap blast 0.46 ms ahead). An unknown cartridge warns once. Renders in
  inbox/gunfire-2026-10-02/.
- Beacons: a door's hangs on its face toward you at face height and rings the room you are in; 4 dB
  louder by default; `/beacons louder` and `quieter`, 2 dB a step, saved.
- Parking: a door found open is left open, none is shut with a player within 2 m, and a rider gets
  off a motorbike without car-door sounds. A rider had shut Brandt Court's front door on Cody.
- The car starter, rebuilt over six rounds of renders (round 5 approved: "much much better"):
  - It is a machine, not a tone: the motor has its own inertia and drives the crank through a
    one-way clutch, which lets go as the crank springs off each compression and clacks picking it
    up, and overruns when the engine catches. Nothing climbs in pitch at the end.
  - Brush and gear noise, pink from 250 Hz to 6 kHz, swelling with load; the ring-gear mesh and an
    11-slot armature whine stand 8-15 dB over it, as in six recorded starts (Freesound, kept in
    inbox/starter-2026-10-02/round4/real/). The noise is 5 dB under that, at Cody's ask.
  - Cranking speed 200 rpm by default (was 250, and the engines actually ran at 500); the key is
    let go at 1.5 times cranking speed, where an engine computer cuts the starter.
  - From the kerb it comes out from under the car, not through the engine bay; from the seat it
    comes through the mounts and floor, low-passed at 150 Hz. Louder with displacement.
  - Mowers fire on their first revolution (a magneto) and crank at 600 rpm (pull cord) and 300.
- The street washes in through an open door:
  - A voice rings its own room from before the walls: the send comes off the input end of its
    chain at the fader's level, not after the route's EQ.
  - Another place's reverb is set by `OpeningRoutes.FieldAt`: each opening radiates its share of
    the field, straight or by the routes, plus what comes in builds the listener's own room's field.
    It replaces a one-opening rule that gave a corridor two openings from the street nothing.
  - Indoors, the street's reverb is traced from 2 m outside the opening it comes in by.
- One speed of sound (the temperature's) for Doppler, echoes, the ground reflection and flight
  time. The wind no longer rides on the listener's velocity and bends every pitch.
- Driving aid tones and lane ticks play in the head: panned by direction, with no HRTF, distance,
  room, reverb or echo.
- The supersonic crack placeholder and the synthesised action clicks were removed; docs/GUNFIRE.md
  says how the crack is to be built.
- Openings come from the geometry (docs/RESEARCH_2026-10-02.md, "Doors and open sides"):
  - Every gap in a room's walls is an opening, as big as the gap and placed in it
    (`FaceOpenings`): open sides, tunnel mouths, doorways with no door. A door is the same opening
    with its leaf in it. Region boxes drawn a little off their walls no longer leave strips of
    "gap" round a room.
  - One opening per gap, not one per pair of rooms. A room with a door and an open side to the
    street has both; two doorways between two rooms are two openings.
  - A composite's uncovered sides and roof are open, not walls, and become openings the same way,
    including for a building put down after the map loaded. The floor is never open.
  - An opening's depth no longer counts a wall that runs through it (a tunnel's side wall made its
    end 50 m deep).
  - City: 524 openings and 14 that disagreed with the geometry before; 679 and none now. Three
    were map faults, now fixed in city.json and gen_city.py: the terminal's road-side steel door and
    the hangar's back door had no hole cut in their walls, and the terminal's north door was linked
    to the middle concourse.
- Server security, written up in docs/SERVER_SECURITY.md:
  - `/where` is for Dev and Admin only. `/profile` shows role, real name (`/realname`), online,
    away (`/afk`) or idle, and the map; never coordinates, direction or distance.
  - New Admin commands: `/sessions`, `/user`, `/throttled`, `/unlock`, `/setrole`.
  - Logins: the rate limit's refill is pinned by a test; IPv6 counts by /64; 10 wrong passwords lock
    a name for 15 minutes; bcrypt runs off the game loop at cost 12; an unknown name costs a bcrypt
    check like a known one; one session per account (a new login closes the old one); a second
    login on one connection is refused.
  - New accounts: 3 to 20 plain characters, reserved names, passwords 8 characters to 72 bytes, and
    3 accounts per address then one every 20 minutes.
  - Connections: nothing but a login is accepted before login; per-address and total connection
    caps on both ports; 2 minutes to log in; 16 KB message limit; MUD lines bounded as they are read
    and written through a queue, so a telnet client that stops reading cannot stall the server.
  - Voice is relayed as from the sender's own body; map data only for the map you are on.
  - `openfps.db` gains seven columns (created, last login and address, failed logins, last failure
    and address, real name). An older database is upgraded in place on first start, after a copy
    is made beside it.
- `StaffGateTests` runs every gated command as a Player and checks nothing changes.
- The client hides "Where is" from players.
- Bugs found by the 2026-10-01 mutation run (docs/MUTATION_2026-10-01.md), each with a test:
  - `/draw` of a name you don't carry took entity 0 out of the map. It is now refused.
  - Disconnecting now puts down what you carry and gets you out of your seat. Before, the items were
    held by a body that no longer existed.
  - "Have a good day." is no longer said at night.
  - Every phone call ends with a goodbye; 11 of 71 calls used to stop mid-call.
  - A level crossing reopens only when the back of the train is clear, not the front.
  - Train sources ride at their own height on a sloping rail.
  - A train leaves a line's only platform, and stops first at the platform ahead of it.
  - Movement input that is not a number is ignored.
- Traffic: `TrafficRuleTests` pins each rule of the road with a scene of its own (walkers at the
  kerb, a car standing in a junction, priority, the car from the right, long vehicles, the deadlock
  breaker). Two faults it found are fixed:
  - a driver giving way reached the line at up to 10.9 m/s instead of its looking speed;
  - the deadlock breaker let every waiting car go at once, and they met in the middle.
- Walkers were driven through at crossings. Three faults, each fixed with a scene test:
  - a car that had started stopping for walkers gave up when it overran its mark;
  - a car stopped for the first crossing in its list, not the nearest;
  - a walker timed a vehicle by its middle, so stepped out in front of a bus nosing up to the strip.
- Map data with a NaN position, or two lanes a hair off parallel, is reported instead of looping
  forever or allocating gigabytes.
- Old code out: the JSON user store, the unused AI state machine, the map publish stub, beacon data
  in every entity state, `PlayerJoined` and `CollisionEvent` messages, four unused components, about
  400 lines of uncalled methods, and the BepuPhysics package.
- More old code out (about 8,000 lines with the docs):
  - the weapons runtime (`WeaponMechanics`, `ShotResolver`), the recorded-blast path
    (`CompositeBlast`, `RecordedBlend`, `FiringTakeIndex`) and the weapon fields only they read
    (magazines, handling times, damage, sound folders). The game fires `WeaponSynth.MuzzleBlast`.
  - `PoliceSirenGenerator` (the siren is `SirenSpec`; its wav stays).
  - the tail A/B switches `OPENFPS_TAIL=full`, `OPENFPS_TAIL_SDM`, `OPENFPS_TAIL_PARAMETRIC`,
    `OPENFPS_TAIL_AMBISONIC` and `OPENFPS_DIFFUSE_TAIL`, with the parametric tail and the ambisonic
    diffuse tail they selected. The default tail is unchanged.
  - `SpatialEmitter.EnableReverb`, which nothing read. Direct sounds send to the reverb and
    reflections do not, as before.
  - `WorldStateUpdate.Season` and `RegionComponent.Environment` (and the prefab field `EnvType`),
    which nothing read.
  - lab spikes and flags: the Steam Audio migration spikes, `BattleSpike`, `GunshotSpike`,
    `AmbientBedSpike`, `EarTest`, `BoundarySpike`, `StreetSceneSpike`, `GripSpike`, and 39 flags in
    all. `AudioLab --help` lists every instrument left. The lab finds the repository one way
    (`LabPaths`).
  - tools: `sabotage-rooms.py`, the footstep synthesis scripts, the first car door fit.
  - docs: STEAM_AUDIO_MIGRATION, CROSS_PLATFORM_PLAN, ROADMAP, NEXT_CLEANSING_PASS, NEXT_THE_CITY,
    VOICES_MACHINES_AND_THE_CITY, NEXT_AFTER_THE_TAIL.
- Both client heads log why they stopped through one helper (`ProcessLifeLog`).
- Dated comments in `ClientGameSession`, `WorldAudioPlayer` and `AsyncAcousticWorker` now say what
  the code does.
- `OpenFPS.Common` changed: the Windows client needs a new build, and the server needs a restart.

## 2026-10-01

- The where-am-I key names a doorway from the zones either side of it ("doorway between <A> and
  <B>"); a zone stops at its walls, so a doorway was in none and was called "Under Shelter". The client
  log records every spoken line ([SAY]) and every zone change ([ZONE]).
- Steam Audio's pathing bake is off: nothing read its answer and it cost a core at every map load.
- Sound comes into a building through its openings. Doorways, open faces and the outdoors are a graph
  (`OpeningRoutes.cs`), each opening sized from the walls round it, with whatever stands in it: a shut
  door passes what its leaf's construction passes, an open one passes everything. A route is the few
  shortest ways through the openings, and what comes along it is the bend at each opening
  (Fresnel-Kirchhoff) plus each room's own sound passed on to the next opening (the building
  acoustics room equation, from the room's surveyed surfaces). Cars, one-off sounds, speech,
  footsteps, birds and beacons all use it, and it competes band by band with what comes through the
  walls; the sound is heard from the doorway when the route wins. In Selby House's ground floor
  corridor, a car 13 m from the front door: front door shut -28/-61/-70 dB (low/mid/high), was
  -52/-89/-100 and did not change when the door opened; front door open -13/-10/-9, heard from the
  stairwell doorway. Only the rooms between count their own sound here: the source's room and yours
  are the reverb's, as before.
- The old portal path is gone. It gave anything with a portal route a flat level in every band, and
  the tracer skipped any wall hit within a doorway's width of a portal: a horn 280 m east of Selby
  House came into the corridor at -1/-3/-6 dB, now -58/-100/-100 through the walls. A horn or siren on
  a car that had not been asked about yet played at full level; it now starts on the one-off path.
- Doors send where their doorway is (the leaf's pose when shut). Rebuild client and server together
  and restart the server: the portal on the wire has new fields.
- `--path-probe` loads the map the way the client gets it and prints the routes and the openings
  near the ear. 14 of the city's 524 openings disagree with the walls (listed when the client loads
  the map): two airport doors with no wall within half a metre of them, a terminal door whose far
  side is another concourse rather than the outdoors, and tunnel and road open faces whose sides are
  not the places they name.

- Traffic stopped skidding at every junction. On their own tyres the cars were taking bends at
  0.46-0.6 g and braking into them at full service brake while the cornering built up, which put the
  front tyres at 0.8-0.95 of their grip: audible squeal in 4 % of the city's driving, against none
  before. Drivers now take a bend at the side friction ordinary drivers find comfortable (the AASHTO
  Green Book's low-speed figures: 0.38 g at 10 mph down to 0.17 g at 40 mph), finish their braking
  on the way in, and pull away out of it gently. Car following uses the ACC model, so creeping up a
  queue no longer stamps on the brakes. Squeal in ordinary driving is back to none; braking stays at
  1-3 m/s2. Server only.
- Each tyre squeals for itself, from its own slip, slip angle and load, and from its own end of the
  car (and louder on its own side for someone close by). A locked wheel dragged at speed is far louder
  than a tyre at its cornering limit; an ordinary stop is silent. Rolling noise is still one voice per
  axle. Client only: no wire change. Listening set in inbox/tyres-2026-10-01/.
- Vehicles stand on their wheels (stage 3 of docs/NEXT_BODIES_WHEELS_ROADS.md). Every preset declares
  its running gear from a real vehicle: tyre sizes, tracks, which wheels drive, steer and brake,
  weight split, centre of gravity height and the lock from its turning circle (`RunningGear.cs`, with
  sources). Each wheel's load moves with braking and cornering, and its force comes from the Magic
  Formula with load sensitivity and a friction circle.
- City traffic is steered along its lanes by a driver and moves under its tyres, instead of being
  placed on the line pointing along it. It takes a bend no faster than the bend really is and than
  keeps its tyres quiet, and pulls away out of a bend with what the cornering leaves. Racers on the
  speedway stay on their line as before.
- Your own car runs on the same wheels.
- Each wheel's load, slip, speed and surface go to the client. The tyre tone uses the tyre's real
  rolling radius (it assumed 0.337 m for everything), and the front and rear tyre voices squeal by
  how hard their own axle is working. Rebuild client and server together and restart the server: the
  entity state on the wire has a new field.

## 2026-09-30

- Mixer safety. A DSP callback with nothing to render writes silence instead of leaving the
  buffer's old contents to be mixed (the master-bus unit passes the mix through). No callback logs or
  allocates on the mixer thread. Pooled EQ, low-pass, synth and granular voices start from rest.
  Shutdown, clearing the reverb buses, changing maps and changing vehicles no longer free Steam
  Audio's native state while the mixer or a tracer may still read it.
- open-fps-patches 0008: a silent late tail (outdoors) is one empty partition, not 2 s of zeros
  convolved every block. All eight patches are in; the patch files were removed.
- Walls let through what their material, thickness and build let through, band by band. Every wall
  heavier than about 50 kg/m2 was a flat 55 dB filter, so a wall made a sound quieter without making
  it duller. Now: the mass law, the coincidence dip from each material's stiffness and thickness
  (Sharp), two-leaf walls with their air gap (stud partitions, glazing), and flanking per band
  (EN 12354-1). Each EQ band takes the figure for the frequencies it actually covers. A 35 cm brick
  wall is -40/-62/-87 dB (low/mid/high), was -55 flat; a stud partition -18/-41/-50, was -54/-55/-55.
- Speech and one-off sounds are occluded by Steam Audio like everything else. They used an older
  tracer that let 10-20 dB more through, made two walls quieter than one and let sound straight down
  through a floor. The tracer, still the fallback, uses the same wall model with no floor under it.
- Partitions are plasterboard on studs and glazing is two panes, as prefab data (`LeafMetres`,
  `StudSpacingMetres`). Lab: `--wall-tl`, and `--path-probe` prints each wall on the line.
- Arriving says "You're in <map>, at <zone>." in one line. The login no longer speaks "Preparing
  manifest". The city's footways are "<street> sidewalk", one name the whole length. "Under Shelter"
  (a roofed gap between two zones, such as a doorway) is no longer announced on its own.
- `ClientWorldState.Clear` takes only the map size.
- Cleansing pass:
  - Deleted: `PhysicsAcousticBridgeSystem` (never called), the unused `users.json` files, and the
    lab's `--sim-roomdbg`, `--echo-ab`, `--blast-compare` and `--blast-probe`.
  - Retired `OPENFPS_ROLLOFF` (the mixer always uses the inverse law Loudness is written for) and
    `OPENFPS_VALVE_K`. Both clients log every set switch from one list, `DiagnosticSwitches`.
  - `run-server.sh` checks and prints the port given with `--port`; `OPENFPS_PORT` is passed to the
    server.
  - Tests seed BirdLife (the sparrow test no longer fails now and then) and delete their
    `/tmp/openfps-test-config-<pid>` folder on exit.
  - Comments in the most-edited files state what the code does and why, without dates and quotes.
    readme and the manual checked against the code; the Windows client is described as it is.
- Sounds no longer freeze for up to a second at a time. Every scene rebuild for a door handed the new
  scene to the tracers while holding the lock the game loop and the mixer take every frame, and the
  late-field tracer holds its own for a whole run; both threads waited it out. Rebuilds are also no
  longer set off by walking: only a leaf near you that has moved since the scene was built counts.
- An open door lets in what is round its corner. The search for a way round an obstacle kept only the
  shortest way round each box, and for a storey-high wall that is over its top, into the slab above;
  the jamb beside it was never tried. Now every way round is tried, shortest first, and the first
  that is clear of the whole scene is taken, all its legs checked. In flat 01F with the door open, a
  walker in the corridor round the corner is -14/-22/-25 dB (low/mid/high); shut, it is the wall's -54.
- When the voice pool is full, a source keeps its last Steam Audio answer for up to a second instead of
  falling back to the hand-rolled tracer, which let highs through walls for a tick.
- `/tail` defaults to -6, by ear.
- `--path-probe ... open=R` measures each source with the doors near the ear shut, then swung open.

- The tail of the place you stand in is its traced late response. It was Steam Audio's parametric
  reverb, which takes three decay times from the trace and nothing else: 14-20 dB too loud in the
  tunnel, silent to 60 ms and then a plateau ("a mask over where the reflections are coming from",
  "an echo over top of the room"). The trace is read back after every run and its late part, faded in
  from 50 to 100 ms, is convolved directly.
- Each source raises its own late sound. The loudest sixteen in your place are traced from where
  they are: how much late sound each raises where you stand, and from which side. A car down the
  tunnel is quieter in the tail than a near one and its tail comes from its side; a room stays even
  all round. Sources not traced follow the place's own fitted law.
- The tail is different at the two ears, as a real diffuse field is: twenty directions through their
  own head responses, and each ear made independent above 400 Hz.
- The tail's first few hundred milliseconds come from the walls they came off (the Spatial
  Decomposition Method): each moment of the traced response plays from the direction it arrives from,
  fixed in the room, so it moves round your head as you turn. In flat 01F most of it comes from the
  ceiling. After about 0.3 s it is spread evenly. `OPENFPS_TAIL_SDM=0` goes back.
- Levels by ear with all of that in: `/tail -12`, `/copies -6` (zero is physical for both).
  `OPENFPS_TAIL_PARAMETRIC=1` plays the old reverb; `/reverb` reports the per-source traces.
- The tunnel's open ends are openings, and a room you walk out of keeps ringing at its own rate.
- Doors are geometry where they are. The sound scene was built once with every door shut, so an open
  door was still a wall; now it is rebuilt with each leaf where it is whenever a door within 50 m
  moves (in the background, about 120 ms on the city). The blanket muffle on outdoor sounds inside a
  closed room is gone: a shut door blocks by its own mass, an open one lets the outside in. Walls lose
  their full mass-law figure (they lost three quarters of it).
- Steam Audio's materials use its own bands (400 Hz, 2.5 kHz, 15 kHz), interpolated from the table's.
- The tail after 0.3 s comes from where the trace's own late sound arrives from: along a corridor,
  along the flat's long axis.

## 2026-09-29

- Reflections were measured at 0 dB before anything else was changed: a clap in flat 01F put its
  placed copies at -14 dB and its traced tail at -17 dB against the direct sound, at or under what room
  acoustics predicts. So -24 was not a level the rooms wanted, and three steps replace it.
  1. Three bugs. A copy's gain used L/d, which is wrong inside the source's reference distance, so
     loud sources' copies were 8-11 dB hot (now max(L,R)/max(d,R), EarlyReflections.PlacedCopyGain).
     Far sources' traced echoes traced the open ground as well as carrying their own ground bounce,
     a comb at about the direct level. The master-bus boundary copies sat outside the trim.
  2. Two levels. `/tail` for everything traced and `/copies` for everything placed as a copy;
     `/reflections` sets both. By ear after step 3: copies 0 dB, the physical level; the tail -24
     (at 0 it was a wash that masked every direction).
  3. Copies as reflections. A surface keeps sqrt(1 - absorption) of the pressure, not 1 - absorption,
     which took twice the decibels. A copy carries only the mirror share, sqrt(1 - scattering) per
     bounce; a first-order wall's scattered share is played as its wash beside it. A room gets its
     first order and at most four second-order copies; the rest is the tail. Your own steps get the
     mirror share and the order limit, not yet the wash.
  Leaving a room no longer cuts its ring off: its bus falls at the room's own measured decay rate.
- Diffraction is the same both ways round an obstacle and exact over thin walls (open-fps-patches 7,
  with a closed-form edge search: 20 us a call). Doorways and low walls lose less.
- The material table is the only source of material values; `materials.json` is gone (a copy is in
  `docs/retired/`).

- The Windows client is brought level with the GTK one. Main menu: Connect, Saved Servers,
  Settings (output and input device, interface sounds), Open log folder, Quit, on the same
  `client.json` format (`%APPDATA%\openfps`). Keys come from the game window instead of a global
  hook, are cleared on every focus change, and Alt on its own no longer opens the system menu.
  Speech goes through NVDA, checked per line, with SAPI when NVDA is not running; the menus speak
  control names only when no screen reader is running. The connect form has Create account. It
  ships `machines/*.json`, which it lacked, so vehicles have engines. Logs go to `logs\` beside
  the exe, and a hang writes a dump there. Compiled from Linux, not yet run on Windows.
- `publish-windows.sh` builds a self-contained Release zip, `publish-server.sh` a server tarball
  for a VPS (no accounts database in it). Both build under `~/.cache/openfps-publish`, not /tmp.
  See `docs/WINDOWS_AND_SERVER.md`.
- Client and server must be built from the same `OpenFPS.Common`. Its sources are hashed at build
  time (`WireContract.Hash`), the client sends the hash with its login, and the server refuses a
  mismatch and says so. An old server cannot say so: it drops the login.
- `OPENFPS_ADMIN_PASSWORD` sets the admin password on a new database and resets it on an existing
  one. The server warns at every start while it is still admin123.
- The cabin of the vehicle you ride in plays its traced response at its traced level again. The
  reflections trim had taken it 24 dB down, and a bus ride was muffled, with the doors and the
  street gone. `/cabin <dB>` sets it for judging by ear.
- Engines are heard to start. Firing waits for the engine computer to synchronise
  (`EngineProfile.RevolutionsBeforeFiring`: three revolutions for petrol, four for diesel), and the
  driver holds the key until it catches. Every preset cranked for 0.02-0.09 s before; now 0.34-0.53 s.
- A bus lets passengers off at its stops only. Stopped at a light or a junction the doors stay shut
  and you are told so. Anything with a door chime works this way; the driver can always get out.

- One rule for every place, and the room algorithm is gone. A one-off sound's first 80 ms are
  placed voices mirrored through the surfaces round it, indoors and out, for claps and shots and
  your own footsteps alike. The listener's traced stage plays only the late tail, everywhere, as a
  diffuse field; other rooms heard through their doorways and vehicle cabins keep their whole
  traced response. Facade echoes and street flutter beyond the window stay separate events
  outdoors; in a room the copies past the window are dense and are the tail. Nothing in the audio
  path decides by "indoors" any more except that last, physical distinction.
- `/reflections -24` is the one level for every reflected sound against the direct: placed copies,
  every traced tail, and the traced echoes of far sources. `/room` and `/echoes -N` set the same
  number. It was reached three times by ear on three mechanisms, in a carpeted flat, a concrete
  tunnel and a street, which is why it is one number and not a per-place one. Zero is the physical
  level, measured. `OPENFPS_REFLECTIONS_DB` starts it; `OPENFPS_TAIL=full` keeps the whole traced
  response outdoors for an A/B against the tail-only rule.
- Retired: `/reverb room`, `OPENFPS_REVERB`, the SFXREVERB tail (the unit stays as a dry
  passthrough the traced stage is inserted at), the Sabine and enclosure estimates behind it, the
  wet-level loop, the room-equation sends, the anisotropy steering of the listener's bus, the
  first-order three-tap footstep echoes, and the labs `--open-air-reverb`, `--tailcheck` and
  `--reverb-route`. The survey's return direction and mean free path are no longer sent to the mixer.

- The reflection search only looks at the boxes that can matter. Every footstep and clap in a room
  searched the whole city (5,220 solids) and tested every leg against all of it: 73-90 ms per
  footstep on the game thread in the flat, which is five frames. A path no longer than the window
  allows lies inside an ellipsoid round the source and the ear, so the search first keeps the boxes
  within that reach and tests legs against those alone: 3.7 ms in the flat, 0.5 on the street.
  `--room-echoes` prints the cost.

- `/room -6` trims the room you are in: its placed early reflections and its late tail together,
  in decibels against the traced level. Rooms only; outdoors and open shelters are unchanged.
  `OPENFPS_ROOM_DB` starts it. The default is -24, set by ear, the same figure as the traced echoes
  outdoors ("-24 dB is where it's at, just like outdoors... everything sounds great and accurate").
  The copy-to-direct arithmetic is within 1.6 dB of physics and the flat's tail within 3 dB of the
  room equation, so the level is not where the 24 dB lives. Both trimmed paths render coherent
  copies of the source from a point; the untrimmed outdoor tail, accepted at its physical level, is
  a dense diffuse response. Next: scatter the placed copies by their wall's scattering, then see how
  far the trim can come back.

- Your own footsteps have the room's reflections again. In traced mode the step's reflections
  returned early, from when the traced response carried the early part; once the room's stage was
  cut to its late tail (parametric, silent for 50 ms) a step got a direct sound, then a tail, and
  nothing from the walls between. Walking the flat was a wash with no reflections in it. Steps now
  get what a clap gets: mirrored through the walls to third order, the twelve loudest inside 80 ms,
  each from its own wall with that wall's colour. The step-echo pool is 48 voices.
- The listener's own reverb bus is no longer re-placed at a point. In traced mode it blended a
  mono copy of the whole bus at the survey's return direction, weighted by the survey's anisotropy,
  on top of a field that is already round the head: a second, one-point room. Room mode keeps it.
- The diffuse tail shares its bass between the ears below 120 Hz, not 300: a step on carpet is
  nearly all below 300 Hz, and a tail identical in both ears there sits in the head whatever the
  rest does. `--sa-encode` at 120 Hz: correlation 0.92 / 0.23 / 0.19 / 0.55 / 0.30 / 0.29 from
  150 Hz to 4.8 kHz, level per band within 5 dB of the tail's (+4.8 at 150-300).

- The room you are in is round you, not in your head. Its late tail (Steam Audio's parametric
  reverb) is one channel, and one channel decoded is the same signal in both ears: heard inside the
  head or straight ahead, and it stayed there when the head turned ("a consolidating of reverb in
  front of me"). The tail is now rendered as the diffuse field it is: eight copies through eight
  different all-pass chains, each encoded into the soundfield from a fixed direction in the world (the
  corners of a cube round the head) and decoded through the HRTF in the listener's frame, so each ear
  hears eight directions through eight head responses and the fine structure turns with the head.
  Below 300 Hz the tail goes to both ears as it is, which is what a diffuse field is on a head there.
  Measured (`--sa-encode`): interaural correlation 0.90 / 0.22 / 0.20 / 0.56 / 0.30 / 0.29 in the
  bands from 150 Hz to 4.8 kHz, where a head in a real diffuse field measures about 0.9, 0.5, 0.2
  and near 0; level per band within 3 dB of the tail's. `OPENFPS_DIFFUSE_TAIL=0` restores the
  one-channel tail.
- Two calibrations of Steam Audio's ambisonics, measured and taken out: its encoder writes W at
  1/sqrt(4 pi) of the input (measured at creation by running noise through an encoder), and its
  binaural decoder sums its virtual loudspeakers' head responses coherently for a signal that is
  the same in all of them, which is +10 dB below 300 Hz and +8 dB to 600 Hz. The old one-channel
  tail had that on it: every room's reverb carried ten decibels of extra bass, which is the boom.
- The traced stages' Steam Audio effects and buffers are released with their buses (they leaked).

- Steam Audio's world is now a true image of the game's. The scene, every trace's source and
  listener and the probe volume were handed over with the game's z, while the listener's frame for
  decoding was handed over with z the other way (Steam Audio's forward is -z). So every traced
  response was decoded facing the wrong way along z: the wall ahead of you answered from behind, and
  with you facing west the wall to the north landed in the left ear instead of the right. Measured
  by `--sa-frame` (a wall to the left, a wall ahead): X/W -0.49 before, +0.49 after; all four
  checks pass. Everything now goes through Phonon.World. Occlusion, transmission and pathing never
  cared which way was forward, which is why it was never noticed.
- In a room, the far walls answer a sound at arm's length. A copy's audibility was judged against
  the direct sound at its true distance, so for your own clap half a metre from your ear any wall
  past a twelve-metre round trip was dropped as inaudible. In Marlow flat 01F, 8.65 by 17.86 m,
  the end walls were never placed; between the side walls' answers (25 ms) and the omnidirectional
  tail (50 ms) there was a hole, then the tail arrived in the middle of the head at a level 3-8 dB
  over the window before it, and held: "like there's a hallway in front of me". Audibility is now
  judged against the direct sound as heard, never nearer than a metre; the gains are unchanged.
  The end wall ahead is placed at 36 ms and its second and third orders fill the window to 50 ms.
  The twelve loudest are voiced, not the first twelve in surface order.
- Somebody else's footsteps start with the wall between you already on them. A step was submitted
  with no occlusion, and the worker's answer for its pooled id came a tick or two later and eased
  in — after the step was over. Every footfall outside a flat played its attack through the brick.
  The step now takes the simulator's result for the nearest source it heard a moment ago, else the
  hand-rolled tracer, at submission, and reverberates in the room the foot is in.
- Read from the 02:53 capture in flat 01F, for the record: the direct clap is equal in both ears;
  the placed reflections (5-50 ms) swing 4-9 dB between the ears with heading; the tail (50-200 ms)
  is equal in both ears to 0.1 dB at every heading, with interaural correlation near 1 below
  300 Hz falling to 0.2-0.3 above 2 kHz, which is what a diffuse field measures. Its decay is about
  1.1 s. The flat is a bare 8.65 by 17.86 by 2.73 m room with plaster walls and ceiling and one
  sofa, and the trace's decay for it is 0.7-0.8 s. That length is the geometry's.

- Reflections are placed at their true level against their source. Every copy (echo, reflection,
  flutter) was handed to the loudness placement as a quieter sound of its own. The placement keeps
  45 % of a level difference, so a reflection 14 dB down came out 6 dB down. Every reflection in
  the game was 4-8 dB too loud against what it copies. Copies now take their source's placement
  and are scaled by what the surface and the longer path kept.
- The room's tail no longer sits on the left. The ear decorrelator's right-ear chain summed to 32
  samples more delay than the left's, so the tail reached the left ear 0.7 ms first on every sound.
  Both chains now sum to 260 samples.
- Walls stop sound by their weight. Transmission through an airtight wall now follows the mass
  law from its density and the box's own thickness, per band, up to 55 dB. Porous materials
  (fences, hedges, grass, crowds, carpet, acoustic tile) keep their table figures. Each face of a
  box carries half its loss, and Steam Audio now counts up to eight surfaces, so two walls in a row
  are both paid for.
- A route round a wall only counts if it is clear. The barrier search measures one box at a time,
  and when its route ran into another wall its level was kept anyway. Now what arrives is what the
  wall lets through, from the source's own bearing. For an hour Steam Audio's pathing eq stood in
  for the level: that is the colour of the bend, near 1.0 on a 150 m route, so sirens and walkers
  behind walls played at full level from straight below (the probe grid's route), fixed in front
  of the listener whichever way they turned.
- The tower flats' doors have a wall over them and leaves that lap their jambs. The doorway cut ran
  floor to ceiling, leaving a 65 cm slot over every shut door, and the 0.9 m leaf sat in a 1.0 m
  opening. A shut flat door now passes -23/-35/-41 dB, where it passed -7/-11/-19.
- `--path-probe ear=x,y,z src=x,y,z` shows what the occlusion worker hands the mixer.
  `OPENFPS_AUDIO_DEBUG=1` adds the raw visibility, transmission and route.

- A clap's early reflections play on time. They were queued while the clap played and sent a whole
  frame later: every reflection in the log was 20-50 ms late. One due 6-25 ms after the clap
  arrived 45-65 ms after it ("the clapping breaks up").
- A sound that can only get round a building by going 156 m out of its way pays for the extra
  distance. The barrier model's 24 dB ceiling left that route at -24 dB in every band, and it beat
  the wall. Walkers outside Marlow flat 01F now come through the brick at the wall's own figures,
  -24/-30/-36 dB, instead of a flat -24. That is still too loud for 35 cm of brick: wall
  transmission comes from a table per material and does not know how thick a wall is.

- A room is its walls first, then its tail. In the room you are in, a one-off sound's early
  reflections (first to third order, the first 80 ms) now play as their own voices. Each is
  mirrored off one wall and placed there through the HRTF, so they move as you turn. The traced
  stage for that room plays only Steam Audio's parametric tail, built from the decay times the
  trace measured, which starts about 50 ms in.
  The traced response on its own is nearly omnidirectional: in Marlow flat 01F its directional
  channels sit about 20 dB under the omni one. The capture showed the two ears 85-95 % alike after a
  clap ("the room sounds narrow... I turn my head and nothing seems to move").
  Other rooms and outdoors keep the full traced response. Sustained sounds (engines, speech) get
  the tail but not the placed reflections.
- Fixed: the distance scaling added earlier today also applied to sounds entering from outside.
  A lorry down the street was sent into the flat's reverb about 19 dB hot, the mix ran at -8 LUFS
  and clipped.

- A room's answer to a sound close to you is quieter, measured through the whole mixer with a new
  lab instrument (`--clap-room`, a clap in Marlow flat 01F). The room came back 1 dB over the clap
  where physics puts it about 10 dB under. There were three causes:
  - The traced reverb decoded through an HRTF made for 1024-sample blocks while running at 256.
    That made it about 3.5 dB hot and the wrong colour. It has its own HRTF now.
  - A sound in the room you are in was sent into that room twice, once as its room and once as
    yours. It is sent once now.
  - The trace is normalised to a source one metre off, and every sound was sent as if it stood
    there. In a closed room the send is now the arriving sound times its distance. Outdoors it is
    unchanged beyond a metre. The measured enclosure blends the two.
  A clap now has the room 4.6 dB under it at game levels and 6.6 dB under with the limiter out of the
  way.
- Footsteps outside a building no longer come through the wall on their attack. A new one-shot
  started on the old hand-rolled path's guess (for the pavement outside Marlow flat 01F, a route
  through the flat's door at -4 dB) and slid to Steam Audio's answer (-24 dB through brick) after
  the loudest part had played. It now starts from Steam Audio's answer for the nearest source it
  heard a moment ago.
- Rooms no longer sound like a stadium. The ear decorrelator added on 09-28 was itself a reverberator:
  six all-passes of up to 13 ms at a feedback of 0.6 turned every click into 100 ms of build-up
  peaking 20-45 ms late. It sat on top of every reflection the room handed back, flats, the
  stairwell and the street alike. Measured on 52 claps in a capture, the room's answer peaked 48 ms
  after the clap in a flat a few metres across. The delays are now 0.16-2.2 ms at 0.5: 90 % of a
  click comes back inside 9 ms, and the ears stay apart (0.14 interaural correlation).
- The airport terminal's acoustic ceiling was being heard as carpet. AcousticTile had the same
  resonance index as Carpet, and region faces are stored by index. It has its own now, and a test
  checks that no two materials share one.

## 2026-09-28

- A room is heard round you, not in the middle of your head. The traced reverb is rebuilt from an
  energy field, and in a diffuse room that is all omnidirectional, so it reached both ears as one
  signal: measured from a capture in 64 Alder Street, 0.8-0.99 interaural correlation in the tail
  where a real room is 0.1-0.5. Each ear now gets its own all-pass chain above 300 Hz (the bottom
  stays shared, as it is in a real room): 0.07 above 1 kHz, 0.87 below 150 Hz, level unchanged.
- The time between beacon soundings is yours: `/beacons every 3`, half a second to ten, saved.
- The airport terminal has a suspended acoustic ceiling (a new AcousticTile material): about 1.2 s
  of reverb through the middle and 2.4 s at the bottom, where the bare concrete rang for 7-10 s.
- Shift+E knocks on the nearest door: three knuckles on wood, built from shaped noise and fitted to a
  recording of real knocks (within about a decibel per octave).
- New beacon tones: soft sine notes, each kind its own shape. A door is two notes rising, an item one
  small ring, a vehicle a low note twice. Exits, stairs and waypoints have designs rendered for
  listening; the map places those beacons with their own sounds for now.
- docs/LISTENING_SPOTS.md: places on the city to check rooms and reflections, with their /tp.
- The sound no longer freezes in big rooms. The room tracer held its lock for the whole of a trace,
  and the game asked it where you were every frame, so every frame waited out the trace: in the
  airport terminal (a 9-second hall) every sound stood still for 680 ms at a time, the game loop ran
  at 8 Hz, footsteps and claps came late or not at all, and the reverb stepped. Where you are is now
  handed over without waiting, for the room tracer and the per-source echo tracer both.
- Jumping indoors no longer throws you through the wall. Collision only pushed sideways, so a head in
  a house's roof slab was pushed out of the roof's footprint through the nearest wall ("I can jump
  over the edge to get out but I can't jump back in"). A body in the air now meets a ceiling and
  stops rising; standing, a beam at head height is still a wall.
- Standing right outside a building is outside it. The zone lookup fell back to a half-metre grid
  that carries a room into the first half-metre past its wall, so against a house you were inside
  it: the walkers beside you in the room, everything else muffled through walls.
- Houses have their front doors on the street. Every house on the estate had its doorway in the
  garden-side wall and a door standing inside its solid front wall; the back door now has a doorway
  too.
- A room answers when a room would: the traced reverb is convolved in 256-sample pieces, and the
  convolution is one of its own blocks late, so a room's first reflection comes 5-8 ms after the
  sound instead of 20-23 (in a car cabin too). With the mixer's 1,024-sample block every room had
  been a separate space off to one side ("reflections centred not around me"). It is also
  second-order ambisonics now (it was first), so the answer comes from round you.
- Where two named zones overlap you are in the smaller one, whichever order the map lists them in,
  for the name Z says and for the room you hear alike.
- No sports bike on the city: the 600 supersport tried today did not sound like one and is gone,
  preset and all. The litre bike stays in the registry with a stock silencer and its own tyres.
- Turbocharged exhausts are heard: a turbine takes about 6 dB off the pulses evenly and scatters only
  the top, where it had been a 260 Hz low-pass passing a third, so every turbo diesel was rumble and
  turbo whine with the exhaust's bark gone before the pipe. The twin-turbo pickups' exhaust end is
  now 21-25 dB over their engine bays at a cruise, and brighter; every turbo vehicle is louder.
- Loud cars in traffic: four of the city's ordinary cars are now a sport compact, a turbo hatch, a
  V8 pickup on Flowmasters and a mild small-block muscle car (94-104 dB at a metre cruising, against
  the stock cars' 87-89). Measured, the other cars and the buses do not have the pickups' fault:
  their exhausts are balanced, and at city speeds a stock car's tyres are as loud as its pipe.
- A horn is heard from the car's front as it is now, not from where its exhaust was a few frames ago.
- Echoes get duller with each bounce: a surface's roughness takes more of the top than the middle,
  more off brick than glass, again at every surface in a chain.
- Back gardens on the north side of Birch Street no longer run over Central Street's pavement and
  road; with the smallest zone winning, 54 steps of that pavement had become "back garden".
- The sports bike sounds like a small engine: a stock silencer (119 dB at a metre flat out to 101,
  about what a stock litre bike makes) and the engine itself heard. A petrol engine's block now
  keeps getting louder above 6,000 rpm, as measured engines do (Anderton's 50 log N); at a bike's
  eleven thousand the engine is as loud as the pipe. Nothing changes at or below 6,000.
- The twin-turbo pickups have the bigger pipes approved by ear (five-inch Duramax, six-inch Cummins)
  and exit at the side ahead of the rear wheel, kerb side. Out of the rear bumper the truck's own
  body stood between the pipe and the pavement until it had passed.
- An echo of a shot, a clap or a slam is the crack itself, coming from the wall. Every echo went
  through a diffuser, and off steel, concrete or glass (the shortest delays) that rang: a
  "processed sounding" copy. The mirror share of a wall's return now plays the sound unchanged; the
  scattered share still comes from points across the face, smeared, which is what gives the echo
  the wall's size.
- T on foot claps your hands, heard by everyone near and answered by the walls. In the driver's seat
  T is still the key.
- Lab: `--ride <preset>` renders the game's vehicle voice through a stop-go ride to a WAV, with
  exhaust and mechanism knobs for trying variants; `--shot-echoes at=x,z [shot=x,z]` lists every echo
  a shot makes on a real map and what each came off.
- The traffic driver no longer lurches. Pulling away on a light throttle it held the clutch out while
  the engine revved free, then closed it in one step: a bike jumped ten km/h in a tenth of a second.
  The clutch is now let in over half a second whenever the engine and gear turn at different speeds,
  the pull-away throttle is rolled on and eases off if the vehicle is ahead of where it should be, the
  speed loop's integral no longer winds up during an overshoot, and slowing below what first gear does
  at idle puts the clutch in instead of letting the idle drive the vehicle on. The muscle car and the
  dirt bike had the same lurch, less often. `--shift-trace <preset>` in the lab prints gear, revs,
  clutch and throttle through a stop-go drive.
- The sports bike changes up in town. On a light throttle it changed up at 85 % of its 11,000 rpm
  torque peak, so it never left first below 90 km/h. It short-shifts at 5,000 now, changes down at
  3,000 rather than 5,000, and pulls away at 2,800 (`Gearbox.CruiseUpshiftRpm`, `Gearbox.LaunchRpm`).
- A car door that sounds like a car door. Fitted to a recording and approved by ear: the slam is four
  hits over 75 ms (first touch, the latch's two catches, a rebound), the cabin answering underneath,
  and the body settling; opening is the handle, the latch letting go and the check strap's detent a
  third of a second later. Every part is noise shaped per octave band. A first version built from
  resonators matched the band levels and was rejected as sounding like an instrument, which is
  what a few fixed modes ringing for half a second are. Used for every car door, player or driver.
- Tests for what the engine mutation run found unchecked: the scripted driver's brake, gear, throttle,
  launch clutch and shifts, the network driver switching the engine off, the valve solver at the ends
  of its range, and the air compressor's knock. A gear shift now closes the throttle on its first
  sample rather than its second.
- Approved recordings and renders moved out of the inbox into `approved/`, with an index.
- The footstep bank is Cody's two complete Foley packs: fifteen surfaces, walk, jog, run, scuff and
  landing for each shoe recorded, about 14,000 takes, every source file named for what it is. The game
  plays ordinary walking and landings from it now, a hair different in pitch and level each step;
  the rest loads on first use. Grass has its own recordings.
- Quieter arrival: the loading steps (preloading, receiving entities, acoustics) are shown and no
  longer spoken; arriving says "Logged in. You are in <map>." and then the zone. A /tp no longer
  replays the arrival; command replies make no chat sound; map and general chat keep their own
  sounds when an admin talks.
- Shift+P says what is in sight, nearest first, measured to the nearest part of each thing, in one
  line; nothing behind a wall, and not the floor you stand on.
- A siren or horn heard round a building no longer flutters: where it is heard from turns at a limited
  rate instead of sliding through your head when the route round the building switches sides, and its
  level comes from how far away it really is.
- Gunshot echoes at their proper level: each echo was being muffled by the very wall it came off
  (its line from you ran through that wall to the mirror image behind it), 40-60 dB down. The echo
  search's distance limits now count the extra path over the direct one, so far shots echo too.
- The sports bike and the dirt bike rev like bikes: their gearing lacked the reduction between crank
  and gearbox, so at city speeds they lugged like a diesel. The dirt bike's exhaust is half a metre.
- Steve joins the street, and two children wait for the schools.
- More people and more to say: glenn and louis join (27 voices, 11,053 lines). A voice without the
  original named lines speaks from its own lines in the same category. People on their own now and
  then mutter, think aloud, read a text out or remark on the weather or the hour when it is true;
  people near a shot or a leant-on horn react. Half the phone calls are recorded calls played
  through, some go to voicemail; strangers passing make small talk or ask the way. About one walker
  in four on a wide pavement walks with somebody, and the two talk to each other when you are near:
  47 conversations between ten pairs of voices.

- People on foot cross the roads. Wherever a walker's line passes over a carriageway (a side street's
  mouth at a corner) is a crossing, found at load: 90 in the city. A walker stops at the kerb and
  waits for a gap as long as the walk across plus 3 s (the Highway Capacity Manual's pedestrian
  critical gap); drivers stop for anybody on a crossing, and a driver arriving at a junction stops for
  somebody who has waited at the kerb for 8 s. After 30 s a walker takes any gap as long as the walk.
  A driver waiting at a junction stands short of the crossing, not on it. Over five minutes of city
  traffic nobody out in the road had a vehicle over them, the longest wait at a kerb was 40 s and the
  longest any vehicle stood still was 30 s. The timings are map data (`StreetLife`).
- Two junction faults the crossings brought out. A long truck holding at the line was taken to be in
  the junction already, because the smoothed line drifts from the lanes by metres at corners, and
  drove on into a car. And "everybody is waiting, one goes" could pull out in front of a car about to
  arrive; it now waits for anything within 4 s.

## 2026-09-27

- Drivers take turns at junctions. A vehicle already in a junction, or too close to stop before the
  line, has it: nobody enters on a path that crosses or joins its path. A driver giving way arrives
  at walking pace to look and goes only if the priority traffic is further off than the Highway
  Capacity Manual's critical gap (6.2 s turning right, 6.5 straight across, 7.1 turning left, 4.1 for a
  left turn off the priority road across oncoming traffic), counted from when it reaches the line.
  A left turn gives way to oncoming traffic; between equals the one on the right goes first; of two
  side by side, the one further back; and if everyone is waiting for someone, one goes after a few
  seconds. Waiting drivers stand with the front bumper at the line. The fixed two-second give-way
  pause is gone. Over ten minutes of city traffic no two vehicles met inside a junction.
- The city's traffic drives the roads. Each car is a tour of lanes and the turns between them, built
  from the road network at load: a seeded wander, turning at random at each junction, so no two take
  the same way and the city is the same every time. The bus has a fixed route that passes both
  shelters on Main Street the right way and stops at them. Southgate's traffic goes round its square
  and waits at both level crossings. Vehicles give way where their road does not have priority (the
  higher class of road, then the longer one), keep to the kerb lane unless another saves distance,
  keep to each lane's speed limit, and keep a gap to the vehicle in front on the same lane whatever
  their route. The drawn downtown loops are gone; only the railway keeps a drawn track.
- Traffic keeps a gap to the vehicle in front, by the Intelligent Driver Model (Treiber, Hennecke and
  Helbing 2000): it eases off to hold a time headway and stops two metres behind a stopped vehicle.
  Before, no vehicle knew another was there, and over three minutes of city traffic 73 pairs drove
  through each other; now none do. The headway and gap are map data (`StreetLife`). Street maps only:
  the speedway still races.
- `--ground-voice --ladder` in the lab renders a line with its ground reflection at the physical
  level, 6, 12 and 20 dB below it, and with the talker and listener moving as standing people do.
- The city's roads are data. Every road is a record (centreline, type, lanes with a direction,
  width and speed limit, surface stretches) written by the same call that lays its asphalt, and the
  27 junctions are found where centrelines meet. The server builds the network at load: every lane
  cut into the stretches between junctions and where each can turn next, and logs any problem. No
  traffic uses it yet. Southgate's streets, which its traffic had always driven on bare ground, are
  laid (Mill Road, Kiln Street, Tanner Road), and its north side is now Dock Street.
- Short recorded impacts hear the ground: a shot, a door, a knock get the surface's answer from
  their mirror image below it, through an HRTF of their own, worked out from the sound's height, the
  listener's and the surface between them. Sounds made at the ground (footsteps) get none: the
  recording has it. Speech was given it too and flanged, summed into the voice's direction and
  again from below (heard 2026-09-27), so voices have none until the missing part is found (see
  todo). `--ground-voice` in the lab renders a line all three ways.
- Pedestrian voices have less low end where they had too much. Each voice's spectrum below 500 Hz is
  compared with a real talker's at the same pitch (Byrne et al. 1994) and the excess cut: tim by up
  to 11 dB below 100 Hz, linda by up to 8 dB below 160 Hz; the shouting drivers and the lighter
  voices are unchanged. `OpenFPS.Common/Speech/voicing.csv` records what each voice was given.
- Every line is matched by loudness (ITU-R BS.1770) instead of RMS. The voices were up to 2.6 dB
  apart to the ear; now they are level, at the same average as before.
- Pedestrians no longer walk inside walls. The map generator read a prefab with no `IsSolid` as not
  solid, where the server reads it as solid, so the Main Street walks ran through the tunnel's
  concrete sides and a Wharf Avenue walk through a concrete wall. From outside the tunnel you heard
  people walking inside the wall. The generator now uses the server's rule; 304 walks instead of 310.
  A test checks every walk against the server's solid entities.
- The birds no longer freeze the sound for up to a second. They looked for their homes again every
  time a pedestrian or car came into or out of range: 16 rays over every roof on the city, up to
  850 ms at once and about 150 ms every 10-30 s, and the flocks were reset each time. They now look
  again only when the scenery changes.
- C no longer says "-0.0". Walking due south moves x by a hair below zero.

## 2026-09-26

### People in the street talk
- The city's pedestrians speak, using 17 recorded voices (1,462 lines). They greet you as you pass,
  say goodbye as you part, apologise if you walk into them, ask if they can help when you stand in
  front of them, talk on the phone, and greet each other.
- Lines depend on the game clock and weather ("Good morning", "Looks like rain", "Cold out here
  today").
- A line is a world sound from the speaker's mouth, placed, blocked and reverberated like any other,
  and it moves with the speaker while they talk. Levels are the ANSI S3.5 speech levels (62 dB at
  1 m for normal speech, 68 dB raised).
- A talker is duller and quieter behind than in front (about -2, -6 and -13 dB in the low, mid and
  high bands straight behind). Speech gets no discrete echo copies, which made a person in the
  street sound as if they were in a building.
- A voice's tone, air loss and room now update while it plays and moves. Before, only its position,
  level and blocking did.
- Pedestrians greet each other only where a player could hear it (within 40 m).
- Text (MUD) players are told the words of anything said within 10 m.
- Of the three voices in the set cloned from real people, seanterry and jimdale are shipped; ben is not.
- The city has 310 pedestrians, generated from the pavements themselves: every pavement is walked,
  split wherever something solid stands on it, and given a person per 30 m, half each way.
- 25 voices from the 2026-09-27 set: 20 people and 5 angry drivers. joel, seanterry, joeb, ben,
  alec, fluke and camel are handed out twice as often. Eleven people have stories (23 in all), which
  they sometimes tell during a phone call. 40 more everyday lines per voice.
- Every car on the street has a driver with a voice. Drivers yell after a hard stop, at a car coming
  across while they wait at a give-way, at anyone standing in the road ahead (they also brake and
  honk), at a car pulling out in front of them, and when held at a level crossing for 25 s. Yells are
  at shouting level (82 dB at 1 m) from the driver's window and move with the car. The Main Street walks stop at the bus
  shelters, which the old walkers passed through.
- `tools/import_npc_voices.py` imports a new set; `--speech-lines` in the lab decodes every line and
  checks its level.

### Turbos
- A turbo spools as soon as the pedal goes down. On the compound-turbo pickups the spool's target
  was the larger of the idle freewheel and the throttle's share, and the throttle's share only
  passed the freewheel at 1,300-1,700 rpm: the whine held flat pulling away. The throttle's share
  now adds to the freewheel. Turbos with no idle freewheel are unchanged.
- The whine's tip-clearance hump is a narrow band of noise (6% wide) at the power the old sine had,
  not a single line.

### Voices sounded like they were in a room outdoors
- The outdoor traced reverb is traced from the listener's head, so it heard the ground under their
  own feet and handed every sound back 10-15 ms late, 2-7 dB under the direct sound (measured from a
  capture). A close voice with a copy that close behind is a small room. The listener's trace is now
  built without the open ground; sources keep their own ground reflection. Street tails come from the
  facades, first arriving at their real delay (48 ms in a 20 m street).
- `docs/WHO_RENDERS_WHAT.md`: which mechanism renders each part of a sound, so nothing is rendered twice.
- `--traced-reverb` in the lab fails if the listener's trace hands anything back inside 25 ms.

### Your own footsteps
- Your footsteps were drowned out on the busy city: everybody's steps shared one pool of twelve
  voices, taken in turn, and other people's steps took your slot before your step could play. Other
  people's steps now have their own pool of 64, are only made within 15 m, compete for a voice by
  level (only yours are pinned), and no longer take the pool for the echoes of your own steps.

### Sirens and traffic
- A distant siren no longer flutters. Its position was set twice a frame, from two places up to
  300 ms apart, and swung between them. `--siren-route` in the lab drives the police car's route
  past a fixed listener.
- Traffic no longer surges through corners. A vehicle reading the speed limit a braking distance
  ahead saw the corner exit before it reached the tightest point, so it accelerated and then braked
  at every bend; it now takes the lowest limit over the whole look-ahead. Trains too. Most audible on
  the diesel pickups.

## 2026-09-24

### What you hear from far away
- Cars voiced from afar (twelve on the city) now go through the same occlusion, vehicle shadowing
  and air absorption as everything else. Before, they reached the listener unblocked and bright at
  any distance: the white-noise wash heard from the edge of the map.
- A bus lying broadside between you and a car at ear height now blocks it. Before, the route round
  it was computed as a straight line through it.
- An engine's echo is darkened by the air over its own, longer path. Before, it was as bright as the
  car, which could make a passing car seem to be on the far side of the street.

### Echoes and the ground
- The echo of a gunshot, a clap or a door is smeared by the roughness of what it came off, like
  engine echoes: glass returns it almost intact, brick smears it over about 20 ms.
- Open ground returns a short wash after a sharp sound, from the ground round the bounce point (about
  -24 dB at 20 ms and -28 dB at 60 ms for a shot 20 m away over dirt). Before, open ground returned
  nothing. Surfaces are now found by their nearest point, so the city's ground and long facades count
  wherever you are.

### Gunfire
- The gunshot is synthesized to a spec measured from real recordings (the NIJ gunshot dataset): a
  pulse and a short burst that fall 20 dB in 2.5-3.5 ms. The old shot took 18-26 ms. Nothing
  recorded is played; what follows the shot comes from the place it is heard in.
- `docs/GUNFIRE.md` has the measurements and the plan.

### Engines
- Engines breathe only the air that comes past the throttle. Before, with the throttle shut, the
  cylinders drew up to 18 times more, and every engine made power on the overrun. Engine braking
  and idle are now physical; the sportbike reaches its shift point; automatic drivers change down
  when floored. Levels at full throttle are unchanged.
- The road V10 changes gear (its gearbox shifted above the engine's redline).
- Mufflers use the engine's own steepening setting, and the engine voices' soft limiter no longer
  clicks on backfires.

### Doors
- Opening a door depends on the leaf's weight and material: the leaf thumps under the latch, and a
  steel door rings where a wooden one does not.

### The map and the client
- You can no longer walk off the edge of the map. Maps can declare where players can walk
  (`PlayMin`, `PlayMax`); the city's is its built ground. The client says "Edge of the map".
- Shift with `[` and `]` switches chat buffers on Linux.
- Voice chat packets and the mic indicator are no longer cut off as they start.
- Every 5 s the client log lists the sounds reaching you loudest, with level per band and route.

### Testing
- Coverage report (`docs/COVERAGE_2026-09-24.md`) and a first mutation-testing round with Stryker.NET
  (`docs/MUTATION_2026-09-24.md`): 149 new tests, and four real defects found and fixed.

### Horns
- Truck, bus and train air horns swell in and fade out cleanly. Before, they were weak and broke up at the start and end of each blast.
- The low-pressure pitch bend is much smaller (6.5% down to 0.8%), and the valve opens in 25 ms and closes in 30 ms (truck and bus 20/25 ms).
- The horn column rings out when the air stops. Before, it was cut off about 35 dB down.
- A chord horn's bells come in one after another on every blast. Before, this never happened.
- Truck and bus horns have more body (the reed is open for less of each cycle).
- Car horns swell in over about 20 ms and ring out over about 50 ms.

### Chat and menus
- Chat messages carry their channel: map, all, private or server. Plain typing reaches your map. `/all` reaches everyone.
- Command answers are no longer labelled "System". Only messages to everyone are labelled "Server".
- Four chat rings: All, Map, Private and Server. `[` and `]` read messages. Shift with them changes ring.
- Admins can use `/announce` and `/setmotd`. Anyone can use `/motd`. `motd.txt` beside the server is read to each player on their first arrival in a session.
- Interface sounds: a tick when focus moves, a rising tone to select, a falling tone to go back, a chord on entering the world, and a sound for each kind of chat.
- F5 (players), F6 (maps) and F8 (friends) open lists. Up and Down move, Enter or Right chooses, Escape, Left or Backspace goes back, and a letter jumps. You stand still while a list is open.
- A player's menu offers private message, where is, view profile, and add or remove friend.
- Linux main menu: Connect goes to your preferred server. Saved Servers lets you add, edit, remove and set a preferred server. Settings has output device, input device and interface sound volume.

### Travelling between maps
- `/join` moves you to another loaded map without logging out. Choosing a map from F6 does the same.
- The loading screen shows during the move.

### Friends
- `/friend add` and `/friend remove`. F8 says which friends are online.
- `/where` and `/profile` give distance, clock direction and place.
- For hosts: friends are kept in `friends.json` beside the server.

### City
- Pavements no longer say "Outside". The four cross streets have their own pavement regions.

### Street life, birds and trains
- Every road vehicle has a horn that suits it. A honk is sent by the server and played on the vehicle.
- On the city: a honk somewhere about every 45 s, a hard stop with squealing tyres about every 2 min, and a car parking beside a door about every 90 s (engine off, door, driver walks in and out, engine on, drives away).
- Trains sound long-long-short-long before every level crossing. The game never played a train horn before.
- Birds are placed by habitat: sparrows in trees, doves, pigeons and crows on roofs, geese flying over. A loud noise or someone walking up makes them go quiet.

### Reverb and reflections
- Moving vehicles block sound from things behind them.
- Engine echoes are back on. Each echo is smeared by the roughness of the wall it came off, so it no longer sounds like a second copy of the car. `OPENFPS_ENGINE_ECHOES=0` turns them off.
- The room survey is about 40% cheaper (55 ms to 33 ms on Main Street).

### Fixes
- Distant cars and machines no longer stutter or cut out behind buildings. Before, they came back unblocked every five seconds.
- The acoustic simulator no longer falls back to the simpler tracer in the apartment building when too many sources are in use.
- Door beacons are no longer silent when you approach at an angle.
- Steel doors make a sound now. The first time a sound was needed it used to be dropped. The steel door is now a hollow door of about 64 kg instead of a solid 2.4 t slab.
- A police siren no longer plays where its car was twenty seconds earlier.
- Voice chat packets play in full. Before, each sound was released as soon as it started.
- Grandstands on two different maps can no longer silence each other's crowd reactions.

### For contributors
- Dead code removed across the audio and spatial code.
- `dotnet-stryker` added as a local tool for mutation testing.
- `--car-horn` takes `air=`, `bend=`, `rise=`, `fall=`, `tag=`. `--door-opening` renders doors before and after.
- `ClientSettings` is in the shared client core, one file per user.
- Planning notes and an audio engine coverage report.

## 2026-09-23

### Beacons
- A beacon is a short sound that says where something is. Each has a category: door, exit, stairs, item, vehicle or waypoint.
- Maps set a policy per category: default on, default off, forced on or forbidden.
- Players switch categories with `/beacons`, `/beacons door`, `/beacons door on|off`. Choices are kept in `~/.config/openfps/beacons.json`.
- Most beacons come from what a thing is. Every door, item and drivable vehicle is a beacon. The city has 470 door beacons without anyone placing them.
- The nearest few of each kind sound: doors within 12 m, items within 10 m, cars within 25 m.
- Beacons blocked by a wall are skipped.
- Doors knock, items ring a bell and vehicles give a low double tone, so no beacon sounds like a crossing chirp.

### Doors
- 71 doors on the city refitted. Tower entrances, terminal doors and estate front doors now sit in their walls and fill the opening.
- 64 house curtains hung across front doors were moved beside them. You can walk through the door at 24 Birch Street.
- Pressing E beside an open door with nothing to get into closes it.

### Reverb and reflections
- Sounds can reflect off two or three surfaces outdoors, so you hear the flutter between two facades. This applies to one-off sounds only, at most three echoes.
- Sustained sounds (jets, air hiss, sirens, machines) use first-order reflections only. Before, they left ghost washes of noise hanging in one place.
- One-off echoes were delayed twice. A facade's slapback now arrives at 90 ms, not 180.
- Under a bus shelter or in the tunnel, outdoor sounds are no longer muffled. The roof check now applies only inside real enclosures.
- The room survey knows where a small room ends. Bus shelter tail 2.2 s down to 0.9 s. Open-deck garage 5.9 s down to 3.2 s. Other places unchanged.
- The idle engine boost no longer lifts bus air brakes, so they do not carry across the city.

## 2026-09-22

### Driving
- Four parked cars in the garage (hatchback, sedan, pickup, muscle car). You can get in and drive.
- T starts the engine. Shift+T switches it off. The starter motor has its own sound.
- Getting in or out opens and closes the car door beside your seat.
- Steering turns at the pace of hands on a wheel, and at speed the lock is limited to what the tyres can hold.
- Driven cars squeal their tyres.
- The arrow keys work as a second set of WASD.
- `/tp` from a seat gets you out first.
- An idling car is now audible. It was about 11 dB under a window air conditioner at the same distance.
- Getting in tells you whether the engine is running. Getting out tells you if you left it running.

### Driving aids
- Guide beep: a high beep on the middle of your lane ahead. Steer until it is in front. It beeps faster at speed.
- Centre line and kerb: parking-sensor beeps from their side within 1.5 m, a steady tone once you are over. Each has its own pitch.
- The road's name is spoken when you turn onto it, with junctions ahead and their exits, road ends and "Off the road".
- Off the road, the guide beep leads back to the nearest road, and the voice says which road and how far.
- Z says road, heading, lane, speed, and how far you are pointing off the road's line.
- A soft click for every 15 degrees the car turns, and a rising chime when you are lined up with the road.
- Lane assist, on by default, K toggles. It steers to the middle of your lane when you are close to the road's line. Your own steering always wins.
- J and L do nothing in a seat. Your ears face where the vehicle faces.
- Driving cues are now actually played. Before, they were dropped anywhere more than 120 m from the middle of the map.

### Cab sound
- Inside a car you hear the engine through the body: the firing note, not the rasp. Wind noise rises with speed.
- The city outside is filtered by the windows, about 21 to 30 dB quieter.
- Your own car door is no longer filtered by its own glass.
- A car's room is its cabin, not the whole car.
- Your ears are a metre above the seat, inside the car.

### Buses
- A bus that stops at bus stops has seats. City bus 1 has 22. You get the nearest free seat.
- Nobody gets on or off above walking pace.
- On the bus you hear the door beeper, and the street comes in through the open doors.

### Vehicles are solid
- You cannot walk through cars, buses, trucks or mowers. Aircraft and pedestrians stay walk-through.
- Each vehicle has its real size. The school bus is 10.9 m long.
- A car that drives into you pushes you aside.

### Crossings, stops and machines
- Crossing bells ring. Before, the bell played as a nameless sound.
- Mowers follow their real speed, and the engine works harder under load. The verge mower now moves.
- Level crossings and route stops for buses and trains.
- Electronic sirens and a road police car.

### Coordinates
- C and `/tp` now use x east, y north, z height.

### Footsteps
- The footstep sounds are twelve walking recordings, 518 samples across twelve materials.

### Fixes
- A driver was told every tick that they were on foot, because large state updates lost the seat when split. Footsteps while driving, missing cab sound and missing lane cues all came from this.
- A driven car no longer climbs on top of its own floor.

## 2026-09-20

### Trains, mowers, walkers and buses
- Two light rail sets run the city loop.
- Push mowers move back and forth across their gardens. You hear the pusher's footsteps.
- Four people walk the pavements, heard by their footsteps.
- Buses and trucks have air brakes: release, spring brakes and doors at a stop, release when moving off.
- The airliner's whine no longer stops at spool-up.
- Your own footsteps are 8 dB louder.

### Fixes
- The city map no longer crashes the client. A reverb send was being disconnected through the wrong reverb unit. Nothing audible changed.
- Only one game window opens. Before, `/tp` could open another.

### For contributors
- New lab commands: `--foreign-disconnect`, `--send-churn [ownroom]`, `--send-window`, `--send-drift`, and `tools/read_core_dsp.py`.
- `run-gtk-client.sh fmodlog` uses FMOD's logging build.
- Region ids in client logs are server ids, not `city.json` ids.

## 2026-09-19

### The city map
- `./run-server.sh city` runs a city block: two three-storey buildings with flats, a corridor and a tiled stairwell, a street, a tunnel, a two-deck parking garage, a bus shelter and a metro platform.
- Named places measure their own materials from the walls around them when the map loads. Maps do not have to set room acoustics.
- New materials: Brick, Asphalt, Tile, Foliage, Plaster, and soft furniture.
- The flats have carpet, plasterboard and furniture, and sound furnished. The stairwell is bright and ringing.
- The city is no longer cold enough to turn footsteps into snow.

### Reverb and reflections
- Rooms now have real reverb tails. The reverb unit had been set to early reflections only. A car park now rings for seconds, a corridor for under half a second.
- Your own footsteps reverberate in the room you are in. Before, they went to the outdoor reverb, so every room sounded the same.
- The reverb level is no longer pulled down in live rooms. Before, a car park's tail was 7 dB quieter than it should be.
- Room surface area is measured, not assumed to be a cube. The garage was 9 dB too reverberant.
- Reverb fades between rooms instead of jumping, and walking past a doorway no longer pops.
- The tunnel, stairwell and metro platform get their full reverb tail back.
- Footsteps get reflections from nearby walls, heard from the wall's direction.
- Echoes arrive after the sound, not at the same moment. Before, they stacked on top of it as one bang.
- Reflections no longer feed the reverb as new sources. This removed a pop on each footstep indoors.
- The bus shelter is no longer a room.

### Movement and footsteps
- Stepping down a kerb no longer counts as a landing. The periodic bangs while standing near buildings were landings.
- No landing sounds on arrival at the spawn point.
- Step length grows with speed, like a real walk. Running no longer sounds like nine steps a second.
- Footsteps are louder: 68 dB instead of 55.
- Every tap of a movement key plays a footstep, even a very short tap.
- Asphalt, brick and foliage use the nearest recorded footstep material.

### Keys
- The trigger is Enter. Control fired the rifle, and screen reader users press Control to stop speech.
- No game action may be bound to Control or Alt. A test checks this.
- An admin firing with empty hands is no longer given a rifle. `/fire akm` still works.

### Breathing
- Breathing is no longer played. The breathing model still drives the exertion readout ("Breathing hard", "Winded").
- Air hiss (breath, door seals, air brakes) is now broad turbulence without a sharp start.

### Sounds
- Clapping matches a real recording: brighter and faster than before.
- The megaphone no longer repeats its announcement.
- Scheduled delays and fades now work. Before, every reflection played in sync with its source.
- Footsteps no longer hit the master limiter.

### Machines
- Push mowers, ride-on mowers and air conditioner units, built from their parts. A mower slows in thick grass and recovers.
- Fans make broadband blade noise as well as tones. Aircraft are unchanged.

### Reflections and sound paths
- Early reflections come from the map's own surfaces. How reverberant a place is comes from a survey of the space around you.
- A blocked sound is heard from the edge it bends around.
- Voices are no longer virtualised by FMOD, which caused popping.
- The server takes `--map <id>`.

### For contributors
- New lab commands: `--tailcheck`, `--enclosure`, `--walk`, `--room-walk`, `--breath`, `--applause compare=DIR`, `--yard`.
- `OPENFPS_AUDIO_DEBUG=1` adds `[FOOT]` and `[WAUDIO]` trace lines. `run-gtk-client.sh capture` sets it.
- `OPENFPS_WEATHER` pins the weather for listening tests.
- The map is generated by `tools/gen_city.py`.
- Planning notes.

## 2026-09-18

### Trains, horns, bells and air
- Trains built from their parts: wheel and rail rolling noise, clatter over rail joints, and curve squeal on tight curves.
- Diesel, electric and steam locomotives.
- Air horns, steam whistles and bells.
- Air brakes, doors and dryers as air escaping through a hole.
- A train is a line of sources, one per bogie, so its level plateaus as it passes and the clatter sweeps along it.
- Trains, horns, whistles, bells and air systems are data files. A map can replace one by name.

### Aircraft
- Jet and propeller aircraft, built from their parts: blades, jets, combustor rumble and whine. Piston aircraft use the car engine model with a propeller load.

### Engines
- Jet noise follows one law for every vehicle. The 2.8 diesel no longer has 15 to 20 dB of hiss at speed.
- The F1 car's two tailpipes are two sources. It no longer sounds like a siren.
- Rough behaviour between 12,300 and 13,300 rpm on the F1 repaired.
- Two F1 cars are back in the speedway field.

### For contributors
- New lab commands: `--aircraft`, `--crossing`, `--models`, `--models export=DIR`.
- `azimuth=`, `dist=` and `pipe=` on `--engine-orders` and `--engine-gallery`.

## 2026-09-17

### Footsteps
- The footstep model is calibrated against real concrete recordings. The worst band is now 9.9 dB off, down from 32. The game still plays recorded footsteps.

### For contributors
- `tools/split_footsteps.py` cuts a walking recording into one file per step, without normalising each step.

## 2026-09-16

### Speedway
- The speedway names its places: Front straight, Turns one and two, Back straight, Turns three and four, Infield and Grandstand.
- The far turn now has acoustics. The map's bounds did not contain it.
- A place is announced when its name changes, not each time you cross into another part of it.
- The field is nineteen machines, including three kinds of motorcycle and diesels with and without turbos.
- The grandstand crowd can now be heard from the spawn.
- The crowd reflects off the stands as a soft wash, not a sharp copy.
- Naming a place no longer makes it sound indoors.

### Engines
- Turbocharged engines have a compressor and a turbine. The fixed tone on the school bus when lifting off is gone (26 dB down).
- Diesels have ignition delay, clatter at idle and go smooth under load. Knock pitch depends on the cylinder size.
- Intakes have throttle hiss, so the F1's airbox resonates.
- New engines: 5.9 Cummins and the International DT466 school bus.
- A car is heard from both ends, exhaust and intake, when you are close.

### Sound priority
- Sounds are ranked by how loud they will be at your ear, not by type. A distant clap no longer cuts out a nearby car.
- A car that loses its voice fades out instead of stopping.
- A distant car no longer gets two Doppler shifts.

### Sounds
- Clapping sounds like hands. Bigger hands are deeper and louder.
- A sound that cannot get a 3D voice no longer jumps to full volume.

### Machines as data
- Vehicles are parts lists. Hosts can add machines in `machines/*.json` beside the maps.

### For contributors
- New lab commands: `--machines`, `--machine-pass`, `--intake-ir`, `--engine-alias`. The rev bench holds its rpm and reports a `structure` column.
- The Room: log line says what the reverb is doing.
- A mechanism-based footstep model exists in the lab (`--footsteps`). It is not used in the game.
- Planning notes.
