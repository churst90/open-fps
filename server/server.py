# server.py
import argparse
import asyncio
import os
from pathlib import Path
import subprocess

from include.custom_logger import CustomLogger
from include.server_console import ServerConsole
from include.network import Network
from include.managers.security_manager import SecurityManager
from include.event_dispatcher import EventDispatcher
from include.registries.map_registry import MapRegistry
from include.registries.user_registry import UserRegistry
from include.event_handlers.user_handler import UserHandler
from include.event_handlers.map_handler import MapHandler
from include.servicers.user_service import UserService
from include.servicers.map_service import MapService
from include.managers.role_manager import RoleManager
from include.server_constants import DEFAULT_HOST, DEFAULT_PORT, VERSION, DEVELOPER_NAME, SERVER_NAME, WEBSITE_URL

class Server:
    def __init__(self, host, port):
        self.version = VERSION
        self.dev_name = DEVELOPER_NAME
        self.server_name = SERVER_NAME
        self.website = WEBSITE_URL
        self.host = DEFAULT_HOST
        self.port = DEFAULT_PORT
        self.logger = CustomLogger('server', debug_mode = False)
        self.network = None
        self.user_reg = None
        self.map_reg = None
        self.event_dispatcher = None
        self.console = None
        self.shutdown_event = asyncio.Event()
        self.security_manager = SecurityManager('../keys/security.key')
        self.user_handler = None
        self.map_handler = None
        self.user_service = None
        self.map_service = None
        self.role_manager = RoleManager.get_instance()

    async def process_message_queue(self):
        while not self.shutdown_event.is_set():
            data = await self.network.message_queue.get()
            await self.process_message(data)

    async def process_message(self, data):
        message_type = data.get('message_type')
        # Directly dispatch the event to the appropriate listeners
        await self.event_dispatcher.dispatch(message_type, data)

    async def setup_security(self):
        print("Setting up the security manager ...")
        self.security_manager.get_instance("security.key")
        await self.security_manager.load_key()
        await self.security_manager.start_key_rotation(30)
        await self.security_manager.ensure_ssl_certificate()

    def setup_permission_checks(self, event_dispatcher, user_registry):
        async def on_permission_check(event_data):
            print("checking permission")
            username = event_data['username']
            permission = event_data['permission']
            follow_up_event = event_data['follow_up_event']
            follow_up_event_data = event_data['event_data']
            follow_up_event_scope = event_data.get('scope')

            user = await user_registry.get_user_instance(username)

            if user and user.has_permission(permission):
                print("user permission check succeeded")
                await event_dispatcher.dispatch(follow_up_event, follow_up_event_data, scope=follow_up_event_scope, recipient_username=username if follow_up_event_scope == 'private' else None)

            else:
                # Permission denied, handle accordingly.
                # Dispatch a permission denied event directly using the dispatch method, suitable for private scope.
                await event_dispatcher.dispatch("permission_denied", {
                    'message_type': "failed",
                    'error': 'Permission denied'
                }, scope='private', recipient_username=username)

        event_dispatcher.subscribe_internal("check_permission", on_permission_check)

    async def start(self):
        print("Initializing server ...")
        print(f"{self.server_name} {self.version}")
        print(f"Developed and maintained by {self.dev_name}. {self.website}")
        print("Type 'help' for a list of available commands")
        await self.setup_security()

        # configure the network object
        self.network = Network.get_instance(self.host, self.port, asyncio.Queue(), self.process_message, self.shutdown_event)
        await self.network.start()
        asyncio.create_task(self.process_message_queue())

        # setup the event dispatcher
        self.event_dispatcher = EventDispatcher.get_instance(network=self.network)
        await self.event_dispatcher.update_network(self.network)

        # setup the registries
        self.user_reg = UserRegistry()
        self.map_reg = MapRegistry()

        # Setup the servicers
        self.user_service = UserService(self.user_reg, self.map_reg)
        self.map_service = MapService(self.map_reg, self.event_dispatcher, self.role_manager)

        # setup the handlers
        self.user_handler = UserHandler(self.event_dispatcher, self.user_service, self.role_manager)
        self.map_handler = MapHandler(self.event_dispatcher, self.map_service)

        self.setup_permission_checks(self.event_dispatcher, self.user_reg)
        await self.map_reg.load_all_maps()
        await self.setup_initial_assets()
        self.console = ServerConsole.get_instance(self, self.user_reg, self.map_reg, self.logger, self.shutdown_event)
        self.console.start()

    async def setup_initial_assets(self):
        # Ensure users and maps directories exist
        users_path = Path('users')
        maps_path = Path('maps')
        users_path.mkdir(exist_ok=True)
        maps_path.mkdir(exist_ok=True)

        # Check if users directory is empty (meaning no users have been created)
        if not any(users_path.iterdir()):
            print("No user data was found. Creating the default admin account with developer priviledges ...")
            # Create the initial admin user
            await self.event_dispatcher.dispatch("user_account_create_request", {
                "username": "admin",
                "password": "adminpass",
                "role": "developer"
            })
            print("Default 'admin' user created. The default password is 'adminpassword'. Please change this for security.")

        # Check if maps directory is empty (meaning no maps have been created)
        if not any(maps_path.iterdir()):
            print("No maps were found. Creating a default map ...")

            await self.event_dispatcher.dispatch("map_create_request", {
                "username": "admin",
                "map_name": "Main",
                "map_size": (0, 10, 0, 10, 0, 10),
                "start_position": (0, 0, 0)
            })

            # Add a tile to the main map
            await self.event_dispatcher.dispatch("map_tile_add_request", {
                "username": "admin",
                "map_name": "Main",
                "tile_position": (0, 10, 0, 10, 0, 0),
                "tile_type": "grass",
                "is_wall": False
            })

            # Add a zone to the main map
            await self.event_dispatcher.dispatch("map_zone_add_request", {
                "username": "admin",
                "map_name": "Main",
                "zone_position": (0, 10, 0, 10, 0, 0),
                "zone_label": "main area",
                "is_safe": True,
                "is_hazard": False
            })

            print("Done initializing the server. The console does not have priviledge restrictions. Type 'help' at any time for a list of all available commands. GG!")

    async def shutdown(self):
        print("Shutting down the server...")
        # Stop key rotation first
        await self.security_manager.stop_key_rotation()
        await self.network.stop()
        if self.console:
            await self.console.stop()
        print("Server shutdown complete.")
        self.shutdown_event.set()

async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", type=str, default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    args = parser.parse_args()
    server = Server(args.host, args.port)
    
    await server.start()  # Start the server
    
    # Wait for the shutdown event to be set before proceeding to shutdown
    await server.shutdown_event.wait()
    
if __name__ == '__main__':
    asyncio.run(main())
