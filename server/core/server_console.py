import asyncio
import threading

class ServerConsole:
    _instance = None

    def __new__(cls, *args, **kwargs):
        if cls._instance is None:
            cls._instance = super(ServerConsole, cls).__new__(cls)
        return cls._instance

    @classmethod
    def get_instance(cls, server, user_reg, map_reg, custom_logger, shutdown_event):
        if cls._instance is None:
            cls._instance = cls(server, user_reg, map_reg, custom_logger, shutdown_event)
        return cls._instance

    def __init__(self, server, user_reg, map_reg, custom_logger, shutdown_event):
        self.server = server
        self.user_reg = user_reg
        self.map_reg = map_reg
        self.logger = custom_logger
        self.shutdown_event = shutdown_event

    async def user_input(self):
        while not self.shutdown_event.is_set():
            # Run the blocking input call in a separate thread
            try:
                command = await asyncio.get_event_loop().run_in_executor(None, input, "server> ")
                await self.process_command(command)
            except asyncio.CancelledError:
                print("User input task was cancelled.")
                break

    async def process_command(self, command):
        if command == "exit":
            await self.server.shutdown()
        elif command == "":
            self.logger.info("")
        elif command == "list players":
            self.users = self.user_reg.get_all_users()
            online_users = [username for username, user_info in self.users.items() if user_info.get('logged_in')]
            if online_users:
                self.logger.info("\nList of online users:\n" + "\n".join(online_users))
            else:
                self.logger.info("No online users found.")
        elif command == "list maps":
            self.maps = self.map_reg.get_all_maps()
            if self.maps:
                map_names = "\n".join(self.maps.keys())
                self.logger.info(f"\nList of all maps on the server:\n{map_names}")
            else:
                self.logger.info("No maps found.")
        elif command == "list users":
            self.users = self.user_reg.get_all_users()
            if self.users:
                user_names = "\n".join(self.users.keys())
                self.logger.info(f"\nList of all user accounts on the server:\n{user_names}")
            else:
                self.logger.info("No user accounts found.")
        elif command == "backup maps":
            self.map_reg.save_maps()
        elif command == "backup users":
            self.user_reg.save_users()
        elif command == "help":
            print("Available server commands:\n"
              "'list users': Lists all users registered with the game, both online and offline.\n"
              "'list maps': List all maps on the server.\n"
              "'list players': Lists online players only.\n"
              "'backup maps': Exports the current state of all maps to disk.\n"
              "'backup users': Exports the current state of all users to disk.\n"
              "'exit': Shuts down the server.\n"
              "'help': Shows this help message.")
        else:
            self.logger.info("Invalid command")
#        except asyncio.CancelledError:
#            print("User input task was cancelled.")
#        finally:
#            print("Exiting user input loop.")

    def start(self):
        self.user_input_task = asyncio.create_task(self.user_input())

    async def stop(self):
        if self.user_input_task:
            try:
#                await self.user_input_task
                self.user_input_task.cancel()
            except asyncio.CancelledError:
                print("User input task stopped.")
