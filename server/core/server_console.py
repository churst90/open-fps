# Third party library imports
import asyncio

class ServerConsole:
    _instance = None

    def __new__(cls, *args, **kwargs):
        if cls._instance is None:
            cls._instance = super(ServerConsole, cls).__new__(cls)
            # Initialize instance.
        return cls._instance

    @classmethod
    def get_instance(cls, server, user_reg, map_reg, custom_logger):
        if cls._instance is None:
            cls._instance = cls(server, user_reg, map_reg, custom_logger)
        return cls._instance

    def __init__(self, server, user_reg, map_reg, custom_logger):
        self.server = server
        self.user_reg = user_reg
        self.users = {}
        self.map_reg = map_reg
        self.maps = {}
        self.logger = custom_logger

    def start_user_input(self):
        self.user_input_task = asyncio.create_task(self.user_input())

    async def user_input(self):
        loop = asyncio.get_event_loop()
        while True:
            command = await loop.run_in_executor(None, input, "server> ")
            if command == "exit":
                await self.server.shutdown()
                break
            if command == "":
                self.logger.info("")
            elif command == "list players":
                self.users = self.user_reg.get_all_users()
                online_users = [username for username, user_info in self.users.items() if user_info.get('logged_in')]
                if online_users:
                    for username in online_users:
                        self.logger.info(f"\nList of online users:\n {username}\n ")
                else:
                    self.logger.info("No online users found.")
            elif command == "list maps":
                self.maps = self.map_reg.get_all_maps()
                if self.maps:
                    for mapname in self.maps.keys():
                        self.logger.info(f"\nList of all maps on the server:\n {mapname}\n ")
                else:
                    self.logger.info("No maps found.")
            elif command == "list users":
                self.users = self.user_reg.get_all_users()
                if self.users:
                    for username in self.users.keys():
                        self.logger.info(f"\nList of all user accounts on the server:\n {username}\n ")
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

