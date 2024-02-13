# Standard library imports
import argparse
import subprocess
import os

# Third party library imports
import asyncio

# Project specific imports
from core.custom_logger import CustomLogger
from core.server_console import ServerConsole
from core.network import Network
from core.server_handler import ServerHandler
from core.security.security_manager import SecurityManager as sm
from core.data import Data
from core.modules.chat import Chat
from core.events.event_dispatcher import EventDispatcher

def generate_self_signed_cert(cert_file='cert.pem', key_file='key.pem'):
    if not os.path.exists(cert_file) or not os.path.exists(key_file):
        subprocess.run([
            'openssl', 'req', '-x509', '-newkey', 'rsa:4096',
            '-keyout', key_file, '-out', cert_file,
            '-days', '365', '-nodes', '-subj', '/CN=localhost'
        ], check=True)

class Server:

    def __init__(self, host, port):
        self.version = "version X.Y"
        self.dev_name = "Developer Name"
        self.server_name = "My FPS Game Server"
        self.website = "https://Mywebsite.com/"
        self.host = host
        self.port = port
        # Ensure SSL certificate is ready
        self.ensure_ssl_certificate()
        self.sm = sm("security.key")
#        self.sm.generate_key()
#        self.sm.save_key()
        self.sm.load_key()
        self.key = self.sm.get_key()
        self.data = Data(self.key)
        self.listen_task = None
        self.user_input_task = None
        self.shutdown_event = asyncio.Event()
        self.online_players = {}
        self.user_accounts = {}
        self.maps = {}
        self.message_queue = asyncio.Queue()
        self.chat = Chat()
        self.network = Network(host, port, self.message_queue, self.process_message)
        self.logger = CustomLogger('server', debug_mode=True)
        self.event_dispatcher = EventDispatcher(self.network, self.maps)
        self.server_handler = ServerHandler(self.online_players, self.user_accounts, self.maps, self.data, self.key, self.network, self.chat, self.event_dispatcher, self.logger)
        self.console = ServerConsole(self.online_players, self.maps, self.user_accounts, self.logger)

    def ensure_ssl_certificate(self, cert_file='cert.pem', key_file='key.pem'):
        if not os.path.exists(cert_file) or not os.path.exists(key_file):
            subprocess.run([
                'openssl', 'req', '-x509', '-newkey', 'rsa:4096',
                '-keyout', key_file, '-out', cert_file,
                '-days', '365', '-nodes', '-subj', '/CN=localhost'
            ], check=True)

    async def process_message(self, data, client_socket):
        # Call the appropriate method from ServerHandler based on message type
        message_type = data.get('type')
        if hasattr(self.server_handler.user, message_type):
            server_handler_method = getattr(self.server_handler.user, message_type)
            await server_handler_method(data, client_socket)
        elif hasattr(self.server_handler, message_type):
            server_handler_method = getattr(self.server_handler, message_type)
            await server_handler_method(data, client_socket)

        else:
            # Handle unknown message type or pass to a default handler
            self.logger.info(f"Unknown message type: {message_type}")

    async def start(self):
        try:
            # Initialization tasks before starting the server
            await self.initialize()
            self.logger.debug("Initialization complete.")

            # Start accepting connections
            self.listen_task = asyncio.create_task(self.network.accept_connections())
            self.logger.info(f"Server started. Listening on {self.host}:{self.port}")

            # Start user input listener
            self.user_input_task = asyncio.create_task(self.console.user_input())
            self.logger.debug("Console ready...")

            # Wait for the network listening task and user input task to complete
            # If either task finishes, this will return.
            done, pending = await asyncio.wait(
                {self.listen_task, self.user_input_task},
                return_when=asyncio.FIRST_COMPLETED
            )

            # If the network task is done, the server was stopped, possibly due to an error
            if self.listen_task in done:
                self.logger.info("Server stopped listening for connections.")
                # Optionally handle the error if the task didn't finish cleanly
                if self.listen_task.exception() is not None:
                    error = self.listen_task.exception()
                    self.logger.info(f"Server listening task stopped with an error: {error}")
                    self.logger.info("Server encountered an error", exc_info=error)

            # If the user input task is done, the admin might have issued a shutdown
            if self.user_input_task in done:
                self.logger.info("No longer accepting user input")
                # If the server is still listening, shut it down
                if not self.listen_task.done():
                    self.listen_task.cancel()
                    self.logger.info("No longer accepting incoming connections")

            # Clean up any pending tasks
            for task in pending:
                task.cancel()

        except Exception as e:
            logging.exception("An unexpected error occurred in the server's start method:", exc_info=e)
            self.logger.info(f"An unexpected error occurred: {e}")

        finally:
            # Clean up and close down the server properly
            await self.shutdown()
            self.logger.info("Server successfully shut down")

    async def initialize(self):
        self.logger.info(f"{self.server_name} {self.version}")
        self.logger.info(f"Developed and maintained by {self.dev_name}. {self.website}")
        self.logger.info("All suggestions and comments are welcome")

        self.logger.info("Loading maps database")
        try:
            self.maps = self.data.load("maps")
            if self.maps == {}:
                self.logger.info("No maps found. Creating the default map")
                await self.server_handler.create_map("Main", (0, 100, 0, 100, 0, 10))
            else:
                self.logger.info("Maps loaded successfully")
        except TypeError:
            raise TypeError("The maps variable is not serializable. It has a data type of " + str(type(self.maps)))

        self.logger.info("Loading users database...")
        try:
            self.user_accounts = self.data.load("users")
            if self.user_accounts == {}:
                self.logger.info("No user database found. Creating the default admin account...")
                await self.server_handler.user.create_user_account({"username": "admin", "password": "admin"}, None)
            else:
                self.logger.info("Users loaded successfully")
        except TypeError:
            raise TypeError("The users variable is not serializable. It has a data type of " + str(type(self.user_accounts)))

    async def shutdown(self):
        self.logger.info("Shutting down the server...")

        # Cancel the network task if it's running
        if self.listen_task and not self.listen_task.done():
            self.listen_task.cancel()
            await asyncio.wait([self.listen_task])  # Wait for the task cancellation to complete

        # Cancel the user input task if it's running
        if self.user_input_task and not self.user_input_task.done():
            self.user_input_task.cancel()
            await asyncio.wait([self.user_input_task])  # Wait for the task cancellation to complete

        # Close the network
        await self.network.close()
        self.shutdown_event.set()

    async def exit(self):
        self.logger.info("Disconnecting all players...")
        if isinstance(self.online_players, dict):
            for data in self.online_players.values():
                data[1].close()
        self.logger.debug("Removing all players from the players list...")
        self.online_players.clear()
        await self.shutdown()

async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", type=str, default="localhost")
    parser.add_argument("--port", type=int, default=33288)
    args = parser.parse_args()

    server_instance = Server(args.host, args.port)
    
    loop = asyncio.get_event_loop()

    try:
        await server_instance.start()
    except KeyboardInterrupt:
        await server_instance.exit()

if __name__ == '__main__':
    asyncio.run(main())