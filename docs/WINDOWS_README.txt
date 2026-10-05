OpenFPS for Windows
===================

Running it
----------
1. Extract the whole zip. Keep every file together: the game needs the DLLs and the ASSETS
   folder beside it.
2. Start NVDA if it is not running. The game speaks through NVDA, and through Windows' own
   voice (SAPI) when NVDA is not running.
3. Run OpenFPS.Client.exe.
4. Use headphones. Every sound is placed around your head.

First connection
----------------
1. Main menu, Connect. With no server saved it opens Saved Servers; choose Add.
2. Name: anything. Server address: host:port as you were given it, for example
   example.com:33288. Username and password: the account you want.
3. Save. If you have no account yet, choose Create account on the main menu: it opens a blank form
   for your preferred server with Create account first. Type the username and password you want and
   press Enter. A username is 3 to 20 characters, a password at least 8. It creates the account,
   logs you in, and saves it as its own entry for that server. If you have an account, choose
   Connect.
4. Tick "Remember password" and later runs go straight in from Connect.

"This client does not match the server"
---------------------------------------
The client and the server must be built from the same version. The server was updated; get the
new zip. BUILD.txt in this folder says which build you have, and the message says which build the
server wants.

Other login messages: "You are already logged in" means your account is in the game somewhere
else. "Too many attempts" means wait a few seconds and try again.

Keys in game
------------
No game key uses Control or Alt, so NVDA's keys keep working.

Moving and looking:
- W A S D or the arrow keys move. Shift with them runs. Space jumps.
- J and L turn, O and K look up and down. A tap turns to the next 45 degrees; hold to sweep.
  Shift with them turns 1 degree.

Finding out:
- C coordinates, F facing, H health, Z area, B breath.
- P what is ahead, Shift+P what is near you.
- Comma and period step through the nearest doors, entrances, stairs, items, people, vehicles or
  places. Shift+comma and Shift+period change which kind.
- N turns the narration of what is ahead on or off.
- I lists what you carry; Shift+I says it in one sentence.

Doing things:
- E interacts: picks up a thing at your feet, opens or shuts a door, gets in or out of a vehicle,
  takes from a dead player's bag, lifts a body.
- Shift+E knocks on a door.
- G picks up, Q drops, Shift+R takes the first thing on your back into your hands.
- R: with a gun in your hands, reload; in a vehicle, the windows; otherwise put what you hold on
  your back.
- Enter fires the gun in your hands; with no gun it interacts like E. Hold Enter on automatic.
- X and Shift+X move the gun's fire selector.
- T claps, or starts the engine in the driver's seat. Shift+T stops the engine.
- V voice chat on or off.

Driving: W accelerates, S brakes and reverses, A and D steer, Space brakes, K lane assist, Z road
and speed.

Scope (the M700), with Num Lock on: keypad star raises it, 8 2 4 6 aim, 5 says what is on the
crosshair, 7 and 9 step through targets, plus and minus zoom, 1 and 3 set the turret, period is the
rangefinder, 0 held holds your breath, slash or Enter fires.

Lists and chat:
- F5 players, Shift+F5 players on this map, F6 maps, Shift+F6 your maps, F8 friends. In a list,
  arrows move, Enter chooses, Escape goes back.
- Slash opens the command and chat line. Plain text goes to your map.
- The bracket keys read chat messages; Shift with them changes buffer.
- Escape opens the game menu: Keep playing, Main menu, Quit.

/help in the command line lists the commands you can use. /help settings lists your own sound
settings. The full manual is docs/MANUAL.md in the OpenFPS source.

If the connection drops the game tries to log back in for a minute, ticking every 3 seconds.

Settings
--------
Main menu, Settings: the output device, the microphone for voice chat, interface sounds, online and
offline sounds, narration of what is ahead, bumps, aim assistance, the interface sound volume, and
"Open log folder". Settings are kept in %APPDATA%\openfps\client.json.

When something goes wrong
-------------------------
Main menu, Settings, "Open log folder". Send the newest client-*.log file from that folder, and say
roughly what time it happened. If the game hung, an openfps-hang file is there as well; send that
too.
