# server.py
import asyncio
import logging
import uuid
from pathlib import Path

from interfaces.event_dispatcher import EventDispatcher
from interfaces.client_message_handler import ClientMessageHandler
from interfaces.console_interface import ConsoleInterface

from infrastructure.storage.file_user_repository import FileUserRepository
from infrastructure.storage.file_map_repository import FileMapRepository
from infrastructure.storage.file_ai_repository import FileAIRepository
from infrastructure.storage.role_manager import RoleManager
from infrastructure.storage.chat_logger import ChatLogger

from domain.physics.collision_manager import CollisionManager
from infrastructure.network.network_server import NetworkServer
from infrastructure.network.connection_manager import ConnectionManager
from infrastructure.security.security_manager import SecurityManager
from infrastructure.security.ssl_manager import SSLManager

from services.user_service import UserService
from services.map_service import MapService
from services.ai_service import AIService
from services.role_service import RoleService
from services.physics_service import PhysicsService
from services.movement_service import MovementService
from services.chat_service import ChatService

logging.basicConfig(level=logging.INFO)

async def main():
    dispatcher = EventDispatcher()
    chat_logger = ChatLogger()

    user_repo = FileUserRepository(users_dir="users_data")
    map_repo = FileMapRepository(maps_dir="maps_data")
    ai_repo = FileAIRepository(ai_dir="ai_data")
    role_mgr = RoleManager.get_instance('roles.json', 'user_roles.json')

    collision_manager = CollisionManager(
        map_repository=map_repo,
        user_repository=user_repo,
        ai_repository=ai_repo
    )

    # Initialize SSLManager for SSL context
    ssl_manager = SSLManager(
        cert_file='keys/cert.pem',
        key_file='keys/key.pem'
    )
    ssl_ctx = ssl_manager.get_ssl_context()

    # Initialize SecurityManager for JWT and role checks
    security_manager = SecurityManager(
        role_manager=role_mgr,
        jwt_secret="SUPER_SECRET_KEY",
        jwt_algorithm="HS256",
        jwt_exp_delta_seconds=3600
    )

    # Initialize ConnectionManager
    connection_manager = ConnectionManager()

    # Create map_service without user_service first
    map_service = MapService(
        event_dispatcher=dispatcher,
        map_repository=map_repo,
        role_manager=role_mgr,
        logger=logging.getLogger("MapService")
    )

    user_service = UserService(
        event_dispatcher=dispatcher,
        user_repository=user_repo,
        map_service=map_service,
        connection_manager=connection_manager,
        security_manager=security_manager
    )
    map_service.user_service = user_service
    map_service.collision_manager = collision_manager

    ai_service = AIService(dispatcher, ai_repo)
    role_service = RoleService(dispatcher, role_mgr)
    physics_service = PhysicsService(dispatcher)
    movement_service = MovementService(dispatcher, user_repo, map_repo, collision_manager, user_service)
    chat_service = ChatService(dispatcher, user_service, map_service, role_mgr, chat_logger, connection_manager=connection_manager)

    # Start all services
    await chat_service.start()
    await movement_service.start()
    await user_service.start()
    await map_service.start()
    await ai_service.start()
    await role_service.start()
    await physics_service.start()

    # Check for default admin user
    all_users = await user_repo.get_all_usernames()
    if not all_users:
        await dispatcher.dispatch("user_account_create_request", {
            "client_id": "server_startup",
            "message": {
                "message_type": "user_account_create_request",
                "username": "admin",
                "password": "adminpass",
                "role": "developer",
                "current_map": "Main"
            }
        })

    # Check for default main map
    all_maps = await map_repo.get_all_map_names()
    if not all_maps:
        tile_key = str(uuid.uuid4())
        tiles = {
            tile_key: {
                "tile_position": (0, 99, 0, 99, 0, 1),
                "tile_type": "grass",
                "is_wall": False
            }
        }

        await dispatcher.dispatch("map_create_request", {
            "client_id": "server_startup",
            "message": {
                "message_type": "map_create_request",
                "username": "admin",
                "map_name": "Main",
                "map_size": (0, 99, 0, 99, 0, 9),
                "start_position": (0, 0, 0),
                "is_public": True,
                "tiles": tiles
            }
        })

    shutdown_event = asyncio.Event()
    console = ConsoleInterface(user_repo, map_repo, dispatcher, shutdown_event)
    await console.start()

    msg_handler = ClientMessageHandler(dispatcher)
    host = "localhost"
    port = 33288

    server = NetworkServer(
        host=host,
        port=port,
        message_handler=msg_handler.handle_message,
        ssl_context=ssl_ctx,
        connection_manager=connection_manager
    )

    await server.start()
    logging.info("Server is running. Type 'exit' in console to shut down.")

    try:
        await shutdown_event.wait()
    except KeyboardInterrupt:
        logging.info("Received KeyboardInterrupt, shutting down server...")

    await console.stop()
    await server.stop()
    logging.info("Server shut down cleanly.")

if __name__ == '__main__':
    asyncio.run(main())
