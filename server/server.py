import argparse
import asyncio
from pathlib import Path
import uuid

# Import consolidated classes
from include.custom_logger import get_logger  # Import the utility function
from include.network import Network
from include.managers.security_manager import SecurityManager
from include.event_dispatcher import EventDispatcher
from include.registries.map_registry import MapRegistry
from include.registries.user_registry import UserRegistry
from include.servicers.user_service import UserService
from include.servicers.map_service import MapService
from include.server_constants import DEFAULT_HOST, DEFAULT_PORT, VERSION, DEVELOPER_NAME, SERVER_NAME, WEBSITE_URL
from include.server_console import ServerConsole  # Make sure you have this import

class Server:
    def __init__(self, host, port):
        self.version = VERSION
        self.dev_name = DEVELOPER_NAME
        self.server_name = SERVER_NAME
        self.website = WEBSITE_URL
        self.host = host
        self.port = port
        self.logger = get_logger('server', debug_mode=False)
        
        # Initialize server components
        self.network = None
        self.event_dispatcher = None
        self.shutdown_event = asyncio.Event()
        self.tasks = []
        
        # Managers, Registries, and Services
        self.security_manager = SecurityManager('../keys/security.key')
        self.map_registry = MapRegistry(logger=self.logger)
        self.user_registry = UserRegistry()
        self.user_service = None
        self.map_service = None
        self.console = None  # Add console

    async def setup_security(self):
        """Initialize the security system."""
        self.logger.debug("Starting security setup...")
        await self.security_manager.load_key()
        await self.security_manager.start_key_rotation(30)
        await self.security_manager.ensure_ssl_certificate()
        self.logger.debug("Security setup complete.")

    async def initialize_components(self):
        """Setup the main components of the server."""
        self.logger.info("Initializing server components...")
        
        # Setup network, event dispatcher, and services
        self.network = Network.get_instance(self.host, self.port, asyncio.Queue(), self.process_message, self.shutdown_event, self.logger)
        self.event_dispatcher = EventDispatcher.get_instance(self.network, self.logger)
        
        # Initialize UserService and MapService
        self.user_service = UserService(self.user_registry, self.map_registry, self.event_dispatcher)
        self.map_service = MapService(self.map_registry, self.event_dispatcher)
        
        await self.network.start()
        await self.event_dispatcher.update_network(self.network)
        
        # Add the message queue task
        self.tasks.append(asyncio.create_task(self.process_message_queue()))
    
    async def process_message_queue(self):
        """Process messages from the network message queue."""
        while not self.shutdown_event.is_set():
            message = await self.network.message_queue.get()
            await self.process_message(message)

    async def process_message(self, data):
        """Process a single message from the network."""
        try:
            message_type = data.get('message_type')
            await self.event_dispatcher.dispatch(message_type, data)
        except Exception as e:
            self.logger.exception(f"Error processing message: {data}")

    async def start(self):
        """Start the server, including all necessary tasks."""
        self.logger.info(f"Starting {self.server_name} {self.version}...")
        await self.setup_security()
        await self.initialize_components()
        
        # Load initial assets and start the console
        await self.map_registry.load_all_maps()
        await self.setup_initial_assets()

        # Start the server console in a separate task
        self.console = ServerConsole.get_instance(self, self.user_registry, self.map_registry, self.logger, self.shutdown_event)
        self.console.start()  # Start the console (handles input in the background)

    async def setup_initial_assets(self):
        """Ensure initial assets like maps and users are in place."""
        users_path = Path('users')
        maps_path = Path('maps')
        users_path.mkdir(exist_ok=True)
        maps_path.mkdir(exist_ok=True)

        if not any(users_path.iterdir()):
            self.logger.warning("No user data found. Creating default admin...")
            await self.event_dispatcher.dispatch("user_account_create_request", {
                "username": "admin",
                "password": "adminpass",
                "role": "developer"
            })

        # Default tiles to add (dictionary format with tile_key as the unique identifier)

        default_tiles = {
            uuid.uuid4(): {
                "tile_position": (0, 0, 0, 1, 1, 1),
                "tile_type": "grass",
                "is_wall": False,
            },
            uuid.uuid4(): {
                "tile_position": (1, 1, 0, 2, 2, 1),
                "tile_type": "dirt",
                "is_wall": False,
            },
            uuid.uuid4(): {
                "tile_position": (2, 2, 0, 3, 3, 1),
                "tile_type": "brick",
                "is_wall": True,
            },
}

        if not any(maps_path.iterdir()):
            self.logger.warning("No maps found. Creating default map...")
            await self.event_dispatcher.dispatch("map_create_request", {
                "username": "admin",
                "map_name": "Main",
                "map_size": (0, 10, 0, 10, 0, 10),
                "start_position": (0, 0, 0),
                "owners": "admin",
                "tiles": default_tiles
            })

    async def shutdown(self):
        """Shutdown the server and cleanup resources."""
        self.logger.info("Shutting down the server...")
        self.shutdown_event.set()
        
        # Stop all tasks
        for task in self.tasks:
            task.cancel()
            try:
                await task
            except asyncio.CancelledError:
                pass
        
        await self.security_manager.stop_key_rotation()
        await self.network.stop()
        self.logger.info("Server shutdown complete.")

async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", type=str, default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    args = parser.parse_args()

    logger = get_logger('main', debug_mode=True)  # Add a logger for the main function
    logger.info("Starting the server...")

    server = Server(args.host, args.port)
    try:
        await server.start()
        await server.shutdown_event.wait()
    except Exception as e:
        logger.exception("An error occurred during server execution.")
    finally:
        await server.shutdown()
        logger.info("Server has been shut down.")

if __name__ == '__main__':
    asyncio.run(main())
