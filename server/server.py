# server.py
import asyncio
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
from infrastructure.security.security_manager import SecurityManager
from infrastructure.security.ssl_manager import SSLManager
from infrastructure.network.network_server import NetworkServer
from infrastructure.network.connection_manager import ConnectionManager
from infrastructure.logging.custom_logger import get_logger

from utils.settings_manager import global_settings

from services.user_service import UserService
from services.map_service import MapService
from services.ai_service import AIService
from services.role_service import RoleService
from services.physics_service import PhysicsService
from services.movement_service import MovementService
from services.chat_service import ChatService

from domain.physics.collision_manager import CollisionManager

async def main():
    global_settings.load_settings()
    debug_mode = global_settings.get("logging.debug_mode", False)
    logger = get_logger("Main", debug_mode=debug_mode)

    logger.info("Loading server components...")

    user_repo = FileUserRepository(logger=get_logger("user_registry", debug_mode))
    map_repo = FileMapRepository(logger=get_logger("map_registry", debug_mode))
    ai_repo = FileAIRepository(logger=get_logger("FileAIRepository", debug_mode))
    role_mgr = RoleManager.get_instance('roles.json', 'user_roles.json')

    collision_manager = CollisionManager(map_repository=map_repo, user_repository=user_repo, ai_repository=ai_repo)

    ssl_manager = SSLManager(cert_file='keys/cert.pem', key_file='keys/key.pem', logger=get_logger("SSLManager", debug_mode))
    ssl_ctx = ssl_manager.get_ssl_context()

    security_manager = SecurityManager(role_manager=role_mgr, jwt_secret=global_settings.get("security.jwt_secret", "SUPER_SECRET_KEY"), logger=get_logger("SecurityManager", debug_mode))
    connection_manager = ConnectionManager()

    dispatcher = EventDispatcher(logger=get_logger("EventDispatcher", debug_mode))
    chat_logger = ChatLogger()

    map_service = MapService(event_dispatcher=dispatcher, map_repository=map_repo, role_manager=role_mgr, logger=get_logger("MapService", debug_mode))
    user_service = UserService(event_dispatcher=dispatcher, user_repository=user_repo, map_service=map_service, connection_manager=connection_manager, security_manager=security_manager, logger=get_logger("UserService", debug_mode))
    map_service.user_service = user_service
    map_service.collision_manager = collision_manager

    ai_service = AIService(dispatcher, ai_repo, logger=get_logger("AIService", debug_mode))
    role_service = RoleService(dispatcher, role_mgr, logger=get_logger("RoleService", debug_mode))
    physics_service = PhysicsService(dispatcher, logger=get_logger("PhysicsService", debug_mode))
    movement_service = MovementService(dispatcher, user_repo, map_repo, collision_manager, user_service, logger=get_logger("MovementService", debug_mode))
    chat_service = ChatService(dispatcher, user_service, map_service, role_mgr, chat_logger, connection_manager=connection_manager, logger=get_logger("ChatService", debug_mode))

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
                "tile_position": (0.0, 99.0, 0.0, 99.0, 0.0, 1.0),
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
                "map_size": (0.0, 99.0, 0.0, 99.0, 0.0, 9.0),
                "start_position": (0.0, 0.0, 0.0),
                "is_public": True,
                "tiles": tiles
            }
        })

    shutdown_event = asyncio.Event()
    console = ConsoleInterface(user_repo, map_repo, dispatcher, shutdown_event, logger=get_logger("ConsoleInterface", debug_mode))
    await console.start()

    host = global_settings.get("network.host", "localhost")
    port = global_settings.get("network.port", 33288)

    msg_handler = ClientMessageHandler(dispatcher, logger=get_logger("ClientMessageHandler", debug_mode))
    server = NetworkServer(
        host=host,
        port=port,
        message_handler=msg_handler.handle_message,
        ssl_context=ssl_ctx,
        connection_manager=connection_manager,
        logger=get_logger("NetworkServer", debug_mode)
    )

    await server.start()
    logger.info(f"{global_settings.get('server.name', 'Open-FPS Server')} version {global_settings.get('server.version', '0.1')} started.")
    logger.info(f"Server running on {host}:{port}. Type 'exit' in console to shut down.")

    try:
        await shutdown_event.wait()
    except KeyboardInterrupt:
        logger.info("Received KeyboardInterrupt, shutting down server...")

    await console.stop()
    await server.stop()
    logger.info("Server shut down complete.")

if __name__ == '__main__':
    asyncio.run(main())
