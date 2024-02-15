# Third party library imports
import asyncio

class ServerConsole:

    def __init__(self, user_reg, map_reg, custom_logger):
        self.user_reg = user_reg
        self.users = self.user_reg.get_all_users()
        self.map_reg = map_reg
        self.maps = self.map_reg.get_all_maps()
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
                    for mapname in self.maps.items():
                        self.logger.info(mapname)  # Assuming mapname is still the key
                else:
                    self.logger.info("No maps found.")
            elif command == "list users":
                if self.users:
                    for username in self.users.keys():
                        self.logger.info(username)
                else:
                    self.logger.info("No user accounts found.")
            elif command == "backup maps":
                self.map_reg.save_maps()
            elif command == "backup users":
                self.user_reg.save_users()
            elif command == "help":
                print("Available server commands:\n 'list users': Lists all users registered with the game, both online and offline.\n 'list maps': List all maps on the server.\n 'list players': Lists online players only.\n 'backup maps': Exports the current state of all maps to disk.\n 'backup users': Exports the current state of all users to disk.\n 'exit': Shuts down the server.\n 'help': Shows this help message.")
            else:
                self.logger.info("Invalid command")

