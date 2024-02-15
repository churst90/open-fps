# Third party library imports
import asyncio

class ServerConsole:

    def __init__(self, user_dict, maps, custom_logger):
        self.users = user_dict
        self.maps = maps
        self.logger = custom_logger

    async def user_input(self):
        loop = asyncio.get_event_loop()
        while True:
            command = await loop.run_in_executor(None, input, "server> ")
            if command == "exit":
                break
            if command == "":
                self.logger.info("")
            elif command == "list players":
                if self.users:
                    for playername in self.users.keys():
                        if playername[1]:
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
                if self.users:
                    for username in self.users.keys():
                        self.logger.info(username)
                else:
                    self.logger.info("No user accounts found.")
            else:
                self.logger.info("Invalid command")

