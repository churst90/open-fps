# interfaces/console_interface.py
import asyncio
import logging

class ConsoleInterface:
    def __init__(self, user_repo, map_repo, event_dispatcher, shutdown_event: asyncio.Event, logger=None):
        self.user_repo = user_repo
        self.map_repo = map_repo
        self.event_dispatcher = event_dispatcher
        self.shutdown_event = shutdown_event
        self.logger = logger or logging.getLogger("ConsoleInterface")
        self.command_queue = asyncio.Queue()

        self.user_input_task = None
        self.command_processor_task = None

    async def start(self):
        self.user_input_task = asyncio.create_task(self._user_input())
        self.command_processor_task = asyncio.create_task(self._command_processor())

    async def stop(self):
        self.logger.info("Stopping console interface...")
        if self.user_input_task:
            self.user_input_task.cancel()
        if self.command_processor_task:
            self.command_processor_task.cancel()
        try:
            await self.user_input_task
        except asyncio.CancelledError:
            pass
        try:
            await self.command_processor_task
        except asyncio.CancelledError:
            pass
        self.logger.info("Console interface stopped.")

    async def _user_input(self):
        # Use asyncio.to_thread to safely call input in a blocking manner, off the main thread
        while not self.shutdown_event.is_set():
            try:
                command = await asyncio.to_thread(input, "server> ")
                await self.command_queue.put(command.strip())
            except (EOFError, KeyboardInterrupt):
                self.logger.info("Console input ended.")
                break
            except asyncio.CancelledError:
                self.logger.debug("User input task cancelled.")
                break

    async def _command_processor(self):
        while not self.shutdown_event.is_set():
            command = await self.command_queue.get()
            await self._process_command(command)

    async def _process_command(self, command: str):
        if command == "exit":
            self.logger.info("Shutdown command received. Stopping the server...")
            self.shutdown_event.set()
        elif command == "":
            pass
        elif command == "list players":
            logged_in_users = await self.user_repo.get_logged_in_usernames()
            if logged_in_users:
                self.logger.info(f"Online users: {', '.join(logged_in_users)}")
            else:
                self.logger.info("No online players.")
        elif command == "list maps":
            map_names = await self.map_repo.get_all_map_names()
            if map_names:
                self.logger.info(f"Maps: {', '.join(map_names)}")
            else:
                self.logger.info("No maps found.")
        elif command == "list users":
            usernames = await self.user_repo.get_all_usernames()
            if usernames:
                self.logger.info(f"All registered users: {', '.join(usernames)}")
            else:
                self.logger.info("No registered users found.")
        elif command == "backup maps":
            await self.map_repo.save_all_maps()
            self.logger.info("Maps backed up successfully.")
        elif command == "backup users":
            await self.user_repo.save_all_users()
            self.logger.info("Users backed up successfully.")
        elif command == "help":
            self._print_help()
        elif command.startswith("server announce "):
            message_text = command[len("server announce "):].strip()
            if message_text:
                # Dispatch a chat_message event with chat_category="server"
                await self.event_dispatcher.dispatch("chat_message", {
                    "client_id": "server_console",
                    "message": {
                        "message_type": "chat_message",
                        "username": "server",
                        "token": "server_token",
                        "chat_category": "server",
                        "text": message_text
                    }
                })
                self.logger.info("Server message sent.")
            else:
                self.logger.warning("No message provided for server announce.")
        else:
            self.logger.warning(f"Unknown command: {command}")

    def _print_help(self):
        help_text = (
            "Available commands:\n"
            "help: Shows this help message.\n"
            "list users: Lists all registered users.\n"
            "list maps: Lists all maps.\n"
            "list players: Lists currently logged-in users.\n"
            "backup maps: Back up map data.\n"
            "backup users: Back up user data.\n"
            "server announce <message>: Sends a server-wide announcement.\n"
            "exit: Shuts down the server.\n"
        )
        self.logger.info(help_text)
