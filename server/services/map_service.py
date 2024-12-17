# services/map_service.py
import logging
from typing import Optional

from domain.maps.map import Map
from infrastructure.storage.map_repository_interface import MapRepositoryInterface
from infrastructure.storage.role_manager import RoleManager
from interfaces.event_dispatcher import EventDispatcher
from infrastructure.logging.custom_logger import get_logger

class MapService:
    def __init__(self, 
                 event_dispatcher: EventDispatcher, 
                 map_repository: MapRepositoryInterface, 
                 role_manager: RoleManager,
                 user_service=None,
                 collision_manager=None,
                 logger: Optional[logging.Logger] = None):
        self.event_dispatcher = event_dispatcher
        self.map_repository = map_repository
        self.role_manager = role_manager
        self.user_service = user_service
        self.collision_manager = collision_manager
        self.logger = logger or get_logger("MapService", debug_mode=False)
        self.logger.debug("MapService initialized.")

    async def start(self):
        await self.event_dispatcher.subscribe("map_create_request", self.handle_map_create_request)
        await self.event_dispatcher.subscribe("map_remove_request", self.handle_map_remove_request)
        await self.event_dispatcher.subscribe("map_tile_add_request", self.handle_map_tile_add_request)
        await self.event_dispatcher.subscribe("map_tile_remove_request", self.handle_map_tile_remove_request)
        await self.event_dispatcher.subscribe("map_zone_add_request", self.handle_map_zone_add_request)
        await self.event_dispatcher.subscribe("map_zone_remove_request", self.handle_map_zone_remove_request)
        await self.event_dispatcher.subscribe("map_join_request", self.handle_map_join_request)
        await self.event_dispatcher.subscribe("map_leave_request", self.handle_map_leave_request)
        await self.event_dispatcher.subscribe("map_physics_update_request", self.handle_map_physics_update_request)
        self.logger.info("MapService subscribed to map-related events.")

    async def _ok(self, event_type: str, client_id: str, data: dict):
        self.logger.debug(f"Dispatching OK event '{event_type}' to client_id='{client_id}' with data={data}")
        await self.event_dispatcher.dispatch(event_type, {
            "client_id": client_id,
            "message": data
        })

    async def _fail(self, event_type: str, client_id: str, reason: str):
        self.logger.debug(f"Dispatching FAIL event '{event_type}' to client_id='{client_id}' reason='{reason}'")
        await self.event_dispatcher.dispatch(event_type, {
            "client_id": client_id,
            "message": {"reason": reason}
        })

    async def _check_auth_and_permission(self, client_id: str, username: str, token: str, permission: str, map_name: Optional[str] = None) -> bool:
        if client_id == "server_startup":
            self.logger.debug("Bypassing auth/permission checks for server_startup client.")
            return True

        if not username or not token:
            self.logger.debug("Missing username or token for permission check.")
            await self._fail(permission + "_fail", client_id, "Missing username or token.")
            return False

        if not self.user_service.is_authenticated(username, token):
            self.logger.debug(f"User '{username}' is not authenticated.")
            await self._fail(permission + "_fail", client_id, "Not authenticated.")
            return False

        if self.role_manager.has_permission(username, permission):
            self.logger.debug(f"User '{username}' has the '{permission}' permission.")
            return True

        if map_name:
            self.logger.debug(f"Checking ownership permissions for user '{username}' on map '{map_name}'.")
            game_map = await self.map_repository.load_map(map_name)
            if not game_map:
                self.logger.debug(f"Map '{map_name}' not found.")
                await self._fail(permission + "_fail", client_id, f"Map '{map_name}' not found.")
                return False

            if username in game_map.owners:
                self.logger.debug(f"User '{username}' is an owner of map '{map_name}'. Access granted.")
                return True
            else:
                self.logger.debug(f"User '{username}' not an owner of map '{map_name}', no permission.")
                await self._fail(permission + "_fail", client_id, "Insufficient permissions (not owner).")
                return False
        else:
            self.logger.debug(f"User '{username}' does not have permission '{permission}' and no ownership applies.")
            await self._fail(permission + "_fail", client_id, "No permission to perform this operation.")
            return False

    async def send_map_state_to_user(self, map_name: str, username: str):
        self.logger.debug(f"Sending map state of '{map_name}' to user '{username}'.")
        game_map = await self.map_repository.load_map(map_name)
        if not game_map:
            self.logger.debug(f"Map '{map_name}' not found, cannot send state.")
            return

        user_client_id = self.user_service.get_client_id_by_username(username)
        if not user_client_id:
            self.logger.debug(f"User '{username}' is not connected, cannot send map state.")
            return

        users_in_map = await self.user_service.user_repository.get_users_in_map(map_name)
        players_info = [{
            "username": u.username,
            "position": u.position,
            "yaw": u.yaw,
            "pitch": u.pitch
        } for u in users_in_map]

        map_data = {
            "map_name": game_map.map_name,
            "map_size": game_map.map_size,
            "start_position": game_map.start_position,
            "is_public": game_map.is_public,
            "owners": game_map.owners,
            "tiles": {k: v.to_dict() for k,v in game_map.tiles.items()},
            "zones": {k: v.to_dict() for k,v in game_map.zones.items()},
            "players": players_info,
            "physics": game_map.physics.to_dict()
        }

        self.logger.debug(f"Dispatching map_state to client_id='{user_client_id}'.")
        await self.event_dispatcher.dispatch("map_state", {
            "client_id": user_client_id,
            "message": map_data
        })

    async def broadcast_to_map(self, map_name: str, event_type: str, data: dict, exclude_username: Optional[str] = None):
        self.logger.debug(f"Broadcasting event '{event_type}' to all users in map '{map_name}', exclude_username='{exclude_username}'.")
        users_in_map = await self.user_service.user_repository.get_users_in_map(map_name)
        for u in users_in_map:
            if exclude_username and u.username == exclude_username:
                continue
            cid = self.user_service.get_client_id_by_username(u.username)
            if cid:
                await self.event_dispatcher.dispatch(event_type, {
                    "client_id": cid,
                    "message": data
                })

    async def handle_map_create_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]

        username = msg.get("username")
        token = msg.get("token", None)
        map_name = msg.get("map_name")
        map_size = msg.get("map_size")
        start_position = msg.get("start_position")
        is_public = msg.get("is_public", True)
        tiles = msg.get("tiles", {})

        self.logger.debug(f"Map create request by client_id='{client_id}', username='{username}', map='{map_name}'.")

        if not await self._check_auth_and_permission(client_id, username, token, "create_map"):
            return

        if not map_name or not map_size or not start_position:
            self.logger.warning("map_create_request missing required fields.")
            await self._fail("map_create_fail", client_id, "Missing required fields.")
            return

        if await self.map_repository.map_exists(map_name):
            self.logger.info(f"Map '{map_name}' already exists.")
            await self._fail("map_create_fail", client_id, f"Map '{map_name}' already exists.")
            return

        from domain.maps.tile import Tile
        new_map = Map(map_name=map_name, map_size=tuple(map_size), start_position=tuple(start_position), is_public=is_public)

        for k, tile_data in tiles.items():
            tile_position = tile_data["tile_position"]
            tile_type = tile_data["tile_type"]
            is_wall = tile_data["is_wall"]
            if not new_map.is_tile_within_bounds(tile_position):
                self.logger.debug(f"Tile {k} out of map bounds.")
                await self._fail("map_create_fail", client_id, f"Tile {k} is out of map bounds.")
                return
            tile = Tile(tile_type, tile_position, is_wall)
            added = await new_map.add_tile(k, tile)
            if added:
                self.logger.debug(f"Tile '{tile_type}' added to map '{map_name}' at {tile_position}.")

        if username:
            await new_map.add_owner(username)
            await new_map.join_map(username)
            self.logger.debug(f"User '{username}' added as owner and joined to map '{map_name}'.")

        success = await self.map_repository.save_map(new_map)
        if success:
            self.logger.info(f"Map '{map_name}' created successfully.")
            await self._ok("map_create_ok", client_id, {"map_name": map_name})
        else:
            self.logger.warning(f"Failed to save map '{map_name}'.")
            await self._fail("map_create_fail", client_id, "Failed to save map.")

    async def handle_map_remove_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        token = msg.get("token")
        map_name = msg.get("map_name")

        self.logger.debug(f"Map remove request for '{map_name}' by user='{username}', client_id='{client_id}'.")

        if not map_name:
            await self._fail("map_remove_fail", client_id, "map_name is required.")
            return

        if not await self._check_auth_and_permission(client_id, username, token, "remove_map", map_name):
            return

        if not await self.map_repository.map_exists(map_name):
            self.logger.debug(f"Map '{map_name}' does not exist.")
            await self._fail("map_remove_fail", client_id, f"Map '{map_name}' does not exist.")
            return

        success = await self.map_repository.remove_map(map_name)
        if success:
            self.logger.info(f"Map '{map_name}' removed successfully.")
            await self._ok("map_remove_ok", client_id, {"map_name": map_name})
        else:
            self.logger.warning(f"Failed to remove map '{map_name}'.")
            await self._fail("map_remove_fail", client_id, "Failed to remove map.")

    async def handle_map_tile_add_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        token = msg.get("token")
        map_name = msg.get("map_name")
        tile_data = msg.get("data", {})
        tile_position = tile_data.get("tile_position")
        tile_type = tile_data.get("tile_type")
        is_wall = tile_data.get("is_wall", False)

        self.logger.debug(f"Map tile add request for map '{map_name}', tile_type='{tile_type}', by user='{username}'.")

        if not await self._check_auth_and_permission(client_id, username, token, "add_tile", map_name):
            return

        if not tile_position or not tile_type:
            await self._fail("map_tile_add_fail", client_id, "Missing tile_position or tile_type.")
            return

        game_map = await self.map_repository.load_map(map_name)
        if not game_map:
            await self._fail("map_tile_add_fail", client_id, f"Map '{map_name}' not found.")
            return

        if not game_map.is_tile_within_bounds(tile_position):
            await self._fail("map_tile_add_fail", client_id, "Tile out of map bounds.")
            return

        from domain.maps.tile import Tile
        import uuid
        tile_key = str(uuid.uuid4())
        tile = Tile(tile_type, tile_position, is_wall)
        added = await game_map.add_tile(tile_key, tile)
        if not added:
            await self._fail("map_tile_add_fail", client_id, "Tile key already exists.")
            return

        if await self.map_repository.save_map(game_map):
            self.logger.debug(f"Tile '{tile_type}' added to map '{map_name}' with key '{tile_key}'.")
            await self._ok("map_tile_add_ok", client_id, {"map_name": map_name, "tile_key": tile_key})
        else:
            self.logger.warning(f"Failed to save map '{map_name}' after adding tile.")
            await self._fail("map_tile_add_fail", client_id, "Failed to save map.")

    async def handle_map_tile_remove_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        token = msg.get("token")
        map_name = msg.get("map_name")
        tile_key = msg.get("tile_key")

        self.logger.debug(f"Map tile remove request for map '{map_name}', tile_key='{tile_key}', by user='{username}'.")

        if not await self._check_auth_and_permission(client_id, username, token, "remove_tile", map_name):
            return

        if not tile_key:
            await self._fail("map_tile_remove_fail", client_id, "Missing tile_key.")
            return

        game_map = await self.map_repository.load_map(map_name)
        if not game_map:
            await self._fail("map_tile_remove_fail", client_id, f"Map '{map_name}' not found.")
            return

        removed = await game_map.remove_tile(tile_key)
        if not removed:
            self.logger.debug(f"Tile '{tile_key}' does not exist in map '{map_name}'.")
            await self._fail("map_tile_remove_fail", client_id, f"Tile '{tile_key}' does not exist.")
            return

        if await self.map_repository.save_map(game_map):
            self.logger.debug(f"Tile '{tile_key}' removed from map '{map_name}'.")
            await self._ok("map_tile_remove_ok", client_id, {"map_name": map_name, "tile_key": tile_key})
        else:
            self.logger.warning(f"Failed to save map '{map_name}' after removing tile.")
            await self._fail("map_tile_remove_fail", client_id, "Failed to save map.")

    async def handle_map_zone_add_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        token = msg.get("token")
        map_name = msg.get("map_name")
        zone_data = msg.get("data", {})
        zone_position = zone_data.get("zone_position")
        zone_label = zone_data.get("zone_label")
        is_safe = zone_data.get("is_safe", False)
        is_hazard = zone_data.get("is_hazard", False)
        zone_type = zone_data.get("zone_type", "normal")
        destination_map = zone_data.get("destination_map")
        destination_coords = zone_data.get("destination_coords")

        self.logger.debug(f"Map zone add request for map '{map_name}', zone_label='{zone_label}', by user='{username}'.")

        if not await self._check_auth_and_permission(client_id, username, token, "add_zone", map_name):
            return

        if not zone_position or not zone_label:
            await self._fail("map_zone_add_fail", client_id, "Missing zone_position or zone_label.")
            return

        game_map = await self.map_repository.load_map(map_name)
        if not game_map:
            await self._fail("map_zone_add_fail", client_id, f"Map '{map_name}' not found.")
            return

        if not game_map.is_zone_within_bounds(zone_position):
            await self._fail("map_zone_add_fail", client_id, "Zone out of map bounds.")
            return

        from domain.maps.zone import Zone
        import uuid
        zone_key = str(uuid.uuid4())
        if destination_coords and isinstance(destination_coords, list):
            destination_coords = tuple(destination_coords)

        new_zone = Zone(
            zone_label=zone_label,
            bounds=tuple(zone_position),
            is_safe=is_safe,
            is_hazard=is_hazard,
            zone_type=zone_type,
            destination_map=destination_map,
            destination_coords=destination_coords
        )

        added = await game_map.add_zone(zone_key, new_zone)
        if not added:
            self.logger.debug(f"Zone key '{zone_key}' already exists in map '{map_name}'.")
            await self._fail("map_zone_add_fail", client_id, "Zone key already exists.")
            return

        if await self.map_repository.save_map(game_map):
            self.logger.debug(f"Zone '{zone_label}' added to map '{map_name}' with key '{zone_key}'.")
            await self._ok("map_zone_add_ok", client_id, {"map_name": map_name, "zone_key": zone_key})
        else:
            self.logger.warning(f"Failed to save map '{map_name}' after adding zone.")
            await self._fail("map_zone_add_fail", client_id, "Failed to save map.")

    async def handle_map_zone_remove_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        token = msg.get("token")
        map_name = msg.get("map_name")
        zone_key = msg.get("zone_key")

        self.logger.debug(f"Map zone remove request for map '{map_name}', zone_key='{zone_key}', by user='{username}'.")

        if not await self._check_auth_and_permission(client_id, username, token, "remove_zone", map_name):
            return

        if not zone_key:
            await self._fail("map_zone_remove_fail", client_id, "Missing zone_key.")
            return

        game_map = await self.map_repository.load_map(map_name)
        if not game_map:
            await self._fail("map_zone_remove_fail", client_id, f"Map '{map_name}' not found.")
            return

        removed = await game_map.remove_zone(zone_key)
        if not removed:
            self.logger.debug(f"Zone '{zone_key}' does not exist in map '{map_name}'.")
            await self._fail("map_zone_remove_fail", client_id, f"Zone '{zone_key}' does not exist.")
            return

        if await self.map_repository.save_map(game_map):
            self.logger.debug(f"Zone '{zone_key}' removed from map '{map_name}'.")
            await self._ok("map_zone_remove_ok", client_id, {"map_name": map_name, "zone_key": zone_key})
        else:
            self.logger.warning(f"Failed to save map '{map_name}' after removing zone.")
            await self._fail("map_zone_remove_fail", client_id, "Failed to save map.")

    async def handle_map_join_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg["username"]
        token = msg["token"]
        map_name = msg["map_name"]

        self.logger.debug(f"Map join request by user='{username}' to map='{map_name}'.")

        if not self.user_service.is_authenticated(username, token):
            self.logger.debug(f"User '{username}' not authenticated for joining map.")
            await self._fail("map_join_fail", client_id, "Not authenticated.")
            return

        user = await self.user_service.user_repository.load_user(username)
        if not user:
            self.logger.debug(f"User '{username}' not found in repository.")
            await self._fail("map_join_fail", client_id, "User not found.")
            return

        game_map = await self.map_repository.load_map(map_name)
        if not game_map:
            self.logger.debug(f"Map '{map_name}' not found for join request.")
            await self._fail("map_join_fail", client_id, f"Map '{map_name}' not found.")
            return

        start_pos = game_map.start_position
        if self.collision_manager and not await self.collision_manager.is_valid_position(map_name, start_pos):
            self.logger.debug(f"Start position {start_pos} blocked on map '{map_name}'.")
            await self._fail("map_join_fail", client_id, "Cannot join map, start position blocked.")
            return

        user.current_map = map_name
        user.position = start_pos
        await self.user_service.user_repository.save_user(user)
        self.logger.debug(f"User '{username}' joined map '{map_name}' at position {start_pos}.")

        await self._ok("map_join_ok", client_id, {"map_name": map_name, "position": user.position})
        await self.send_map_state_to_user(map_name, username)

    async def handle_map_leave_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg["username"]
        token = msg["token"]

        self.logger.debug(f"Map leave request by user='{username}'.")

        if not self.user_service.is_authenticated(username, token):
            self.logger.debug(f"User '{username}' not authenticated for leaving map.")
            await self._fail("map_leave_fail", client_id, "Not authenticated.")
            return

        user = await self.user_service.user_repository.load_user(username)
        if not user:
            self.logger.debug(f"User '{username}' not found in repository for leave request.")
            await self._fail("map_leave_fail", client_id, "User not found.")
            return

        if user.current_map:
            self.logger.debug(f"User '{username}' leaving map '{user.current_map}'.")
            await self.broadcast_to_map(user.current_map, "player_left_map", {"username": username}, exclude_username=username)

        user.current_map = None
        user.position = (0.0,0.0,0.0)
        await self.user_service.user_repository.save_user(user)
        self.logger.debug(f"User '{username}' successfully left the map.")

        await self._ok("map_leave_ok", client_id, {"message": "Left the map."})

    async def handle_map_physics_update_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg["username"]
        token = msg["token"]
        map_name = msg["map_name"]
        gravity = msg.get("gravity")
        air_resistance = msg.get("air_resistance")
        friction = msg.get("friction")

        self.logger.debug(f"Map physics update request for '{map_name}' by user='{username}'.")

        if not await self._check_auth_and_permission(client_id, username, token, "modify_physics", map_name):
            return

        game_map = await self.map_repository.load_map(map_name)
        if not game_map:
            self.logger.debug(f"Map '{map_name}' not found for physics update.")
            await self._fail("map_physics_update_fail", client_id, f"Map '{map_name}' not found.")
            return

        if gravity is not None:
            game_map.physics.gravity = gravity
        if air_resistance is not None:
            game_map.physics.air_resistance = air_resistance
        if friction is not None:
            game_map.physics.friction = friction

        if await self.map_repository.save_map(game_map):
            self.logger.debug(f"Physics updated for map '{map_name}': gravity={gravity}, air_resistance={air_resistance}, friction={friction}")
            await self._ok("map_physics_update_ok", client_id, {
                "map_name": map_name,
                "physics": game_map.physics.to_dict()
            })
            await self.broadcast_to_map(map_name, "map_physics_update", {
                "map_name": map_name,
                "physics": game_map.physics.to_dict()
            })
        else:
            self.logger.warning(f"Failed to save updated physics for map '{map_name}'.")
            await self._fail("map_physics_update_fail", client_id, "Failed to save updated physics.")
