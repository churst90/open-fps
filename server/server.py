# Standard library imports
import argparse
import subprocess
import os

# Third party library imports
import asyncio
import logging

# Project specific imports
from serverconsole import ServerConsole
from connection import Network
from serverhandler import ServerHandler
from securitymanager import SecurityManager as sm
from data import Data
from chat import Chat

def generate_self_signed_cert(cert_file='cert.pem', key_file='key.pem'):
    if not os.path.exists(cert_file) or not os.path.exists(key_file):
        subprocess.run([
            'openssl', 'req', '-x509', '-newkey', 'rsa:4096',
            '-keyout', key_file, '-out', cert_file,
            '-days', '365', '-nodes', '-subj', '/CN=localhost'
        ], check=True)

class Server:

    def __init__(self, host, port):
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
        self.server_handler = ServerHandler(self.online_players, self.user_accounts, self.maps, self.data, self.key, self.network, self.chat)
        self.console = ServerConsole(self.online_players, self.maps, self.user_accounts)

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
            print(f"Unknown message type: {message_type}")

    async def start(self):
        try:
            # Initialization tasks before starting the server
            await self.initialize()
            print("Initialization complete.")

            # Start accepting connections
            self.listen_task = asyncio.create_task(self.network.accept_connections())
            print(f"Server started and listening on {self.host}:{self.port}")

            # Start user input listener
            self.user_input_task = asyncio.create_task(self.console.user_input())
            print("Console ready...")

            # Wait for the network listening task and user input task to complete
            # If either task finishes, this will return.
            done, pending = await asyncio.wait(
                {self.listen_task, self.user_input_task},
                return_when=asyncio.FIRST_COMPLETED
            )

            # If the network task is done, the server was stopped, possibly due to an error
            if self.listen_task in done:
                print("Server stopped listening for connections.")
                # Optionally handle the error if the task didn't finish cleanly
                if self.listen_task.exception() is not None:
                    error = self.listen_task.exception()
                    print(f"Server listening task stopped with an error: {error}")
                    logging.error("Server encountered an error", exc_info=error)

            # If the user input task is done, the admin might have issued a shutdown
            if self.user_input_task in done:
                print("No longer accepting user input")
                # If the server is still listening, shut it down
                if not self.listen_task.done():
                    self.listen_task.cancel()
                    print("No longer accepting incoming connections")

            # Clean up any pending tasks
            for task in pending:
                task.cancel()

        except Exception as e:
            logging.exception("An unexpected error occurred in the server's start method:", exc_info=e)
            print(f"An unexpected error occurred: {e}")

        finally:
            # Clean up and close down the server properly
            await self.shutdown()
            print("Server successfully shut down")

    async def initialize(self):
        print("Open Life FPS game server")
        print("Developed and maintained by Cody Hurst, https://CodyHurst.com")
        print("All suggestions and comments are welcome")

        print("Loading maps database")
        try:
            self.maps = self.data.load("maps")
            if self.maps == {}:
                print("No maps found. Creating the default map")
                await self.server_handler.create_map("Main", (0, 100, 0, 100, 0, 10))
#                self.data.export(self.maps, "maps")
#                print("Maps exported successfully.")
            else:
                print("Maps loaded successfully")
        except TypeError:
            raise TypeError("The maps variable is not serializable. It has a data type of " + str(type(self.maps)))

        print("Loading users database...")
        try:
            self.user_accounts = self.data.load("users")
            if self.user_accounts == {}:
                print("No user database found. Creating the default admin account...")
                await self.server_handler.user.create_user_account({"username": "admin", "password": "admin"}, None)
            else:
                print("Users loaded successfully")
        except TypeError:
            raise TypeError("The users variable is not serializable. It has a data type of " + str(type(self.user_accounts)))

    async def shutdown(self):
        print("Shutting down the server...")

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
        print("Disconnecting all players...")
        if isinstance(self.online_players, dict):
            for data in self.online_players.values():
                data[1].close()
        print("Removing all players from the players list...")
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