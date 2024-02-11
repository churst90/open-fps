# Third party library imports
import asyncio

class ServerConsole:

    def __init__(self, players, maps, users):
        self.online_players = players
        self.maps = maps
        self.user_accounts = users

    def update_maps(self, maps):
        self.maps = maps

    def update_users(self, users):
        self.user_accounts = users

    def update_online_players(self, online_players):
        self.online_players = online_players

    async def user_input(self):
        loop = asyncio.get_event_loop()
        while True:
            command = await loop.run_in_executor(None, input, "server> ")
            if command == "exit":
                break
            if command == "":
                print("")
            elif command == "list players":
                if self.online_players:
                    for playername in self.online_players.keys():
                        print(playername)
                else:
                    print("No online users found.")
            elif command == "list maps":
                if self.maps:
                    for mapname in self.maps.keys():
                        print(mapname)
                else:
                    print("No maps found.")
            elif command == "list users":
                if self.user_accounts:
                    for username in self.user_accounts.keys():
                        print(username)
                else:
                    print("No user accounts found.")
            elif command == "list items":
                try:
                    print(self.maps)
                except:
                    print("Couldn't load the dictionary")
            else:
                print("Invalid command")

