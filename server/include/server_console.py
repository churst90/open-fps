import asyncio

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
        self.user_input_task = None
        self.command_queue = asyncio.Queue()  # Queue to manage commands

    async def user_input(self):
        while not self.shutdown_event.is_set():
            try:
                # Non-blocking input using run_in_executor
                command = await asyncio.get_event_loop().run_in_executor(None, input, "server> ")
                await self.command_queue.put(command)  # Add command to the queue
            except asyncio.CancelledError:
                self.logger.debug("User input task was cancelled.")
                break

    async def process_command(self, command):
        """Processes a single command."""
        try:
            self.logger.info(f"Processing command: {command}")
            if command == "exit":
                self.logger.info("Shutdown command received. Stopping the server...")
                await self.server.shutdown()
            elif command == "":
                self.logger.debug("Empty command received.")
            elif command == "list players":
                self.logger.info("Fetching player list...")
                online_users = self.user_reg.get_logged_in_usernames()
                if online_users:
                    self.logger.info(f"List of online users: {', '.join(online_users)}")
                else:
                    self.logger.info("No online players found.")
            elif command == "list maps":
                self.logger.info("Fetching map list...")
                map_names = self.map_reg.get_all_map_names()
                if map_names:
                    self.logger.info(f"List of maps: {', '.join(map_names)}")
                else:
                    self.logger.info("No maps found.")
            elif command == "list users":
                self.logger.info("Fetching user list...")
                user_names = self.user_reg.get_all_usernames()
                if user_names:
                    self.logger.info(f"List of all user accounts: {', '.join(user_names)}")
                else:
                    self.logger.info("No users registered.")
            elif command == "backup maps":
                self.logger.info("Backing up maps...")
                await self.map_reg.save_all_maps()
                self.logger.info("Maps successfully backed up.")
            elif command == "backup users":
                self.logger.info("Backing up users...")
                await self.user_reg.save_all_users()
                self.logger.info("Users successfully backed up.")
            elif command == "help":
                self.logger.info("Displaying help menu.")
                print("Available server commands:\n"
                      "'list users': Lists all users registered with the game, both online and offline.\n"
                      "'list maps': List all maps on the server.\n"
                      "'list players': Lists online players only.\n"
                      "'backup maps': Exports the current state of all maps to disk.\n"
                      "'backup users': Exports the current state of all users to disk.\n"
                      "'exit': Shuts down the server.\n"
                      "'help': Shows this help message.")
            else:
                self.logger.warning(f"Invalid command received: {command}")
        except Exception as e:
            self.logger.error(f"Error processing command '{command}': {e}", exc_info=True)

    async def command_processor(self):
        """A task to process commands from the queue asynchronously."""
        while not self.shutdown_event.is_set():
            command = await self.command_queue.get()  # Get command from queue
            await self.process_command(command)

    def start(self):
        """Starts the user input task and the command processing task."""
        self.logger.info("Starting server console.")
        self.user_input_task = asyncio.create_task(self.user_input())
        self.command_processor_task = asyncio.create_task(self.command_processor())

    async def stop(self):
        """Cancels the user input and command processing tasks when the server is stopping."""
        self.logger.info("Stopping server console...")
        if self.user_input_task:
            self.user_input_task.cancel()
        if self.command_processor_task:
            self.command_processor_task.cancel()
        try:
            await self.user_input_task
            await self.command_processor_task
        except asyncio.CancelledError:
            self.logger.debug("User input and command processor tasks were cancelled.")
        self.logger.info("Server console stopped.")
