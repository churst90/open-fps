OpenFPS for Windows
===================

Running it
----------
1. Extract the whole zip. Keep every file together: the game needs the DLLs and the ASSETS
   folder beside it.
2. Start NVDA if it is not running. The game speaks through NVDA, and through Windows' own
   voice (SAPI) when NVDA is not running.
3. Run OpenFPS.Client.exe.

First connection
----------------
1. Main menu, Connect. With no server saved it opens Saved Servers; choose Add.
2. Name: anything. Server address: host:port as you were given it, for example
   example.com:33288. Username and password: the account you want.
3. Save. If you have no account yet, choose Create account on the main menu: it opens the form
   for your preferred server with Create account first. Type the username and password you want and
   press Enter. It creates the account and logs you in. If you have an account, choose Connect.
4. Tick "Remember password" and later runs go straight in from Connect.

In game
-------
W A S D move, J and L turn, O and K look up and down, Space jump.
C coordinates, F facing, H health, Z area, comma look ahead, E interact, P scan, I inventory.
G take, Q drop, R put on your back, Shift+R draw, Enter fire.
V voice chat, F5 players, the bracket keys read chat, slash opens the command console.
Escape opens the game menu: Keep playing, Main menu, Quit.
If the connection drops the game tries to log back in for a minute, ticking every 3 seconds.
/help in the command console lists the commands.

Settings (main menu) chooses the output device, the microphone, and the interface sounds, and has
"Open log folder".

"This client does not match the server"
---------------------------------------
The server was updated. Get the new zip; BUILD.txt in this folder says which build you have.

When something goes wrong
-------------------------
Main menu, Settings, "Open log folder". Send the newest client-*.log file from that folder, and say roughly
what time it happened. If the game hung, an openfps-hang file is there as well; send that too.
