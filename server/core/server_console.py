# Third party library imports
import asyncio

class ServerConsole:

    def __init__(self, players, maps, users, custom_logger):
        self.online_players = players
        self.maps = maps
        self.user_accounts = users
        self.logger = custom_logger

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
                self.logger.info("")
            elif command == "list players":
                if self.online_players:
                    for playername in self.online_players.keys():
                        self.logger.info(playername)
                else:
                    self.logger.info("No online users found.")
            elif command == "list maps":
                if self.maps:
                    for mapname in self.maps.keys():
                        self.logger.info(mapname)
                else:
                    self.logger.info("No maps found.")
            elif command == "list users":
                if self.user_accounts:
                    for username in self.user_accounts.keys():
                        self.logger.info(username)
                else:
                    self.logger.info("No user accounts found.")
            elif command == "list items":
                try:
                    self.logger.info(self.maps)
                except:
                    self.logger.info("Couldn't load the dictionary")
            else:
                self.logger.info("Invalid command")

