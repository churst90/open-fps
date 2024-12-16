# services/map_service.py
import logging
from typing import Optional

from domain.maps.map import Map
from infrastructure.storage.map_repository_interface import MapRepositoryInterface
from infrastructure.storage.role_manager import RoleManager
from interfaces.event_dispatcher import EventDispatcher

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
        self.logger = logger or logging.getLogger("MapService")

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

    async def _ok(self, event_type: str, client_id: str, data: dict):
        await self.event_dispatcher.dispatch(event_type, {
            "client_id": client_id,
            "message": data
        })

    async def _fail(self, event_type: str, client_id: str, reason: str):
        await self.event_dispatcher.dispatch(event_type, {
            "client_id": client_id,
            "message": {"reason": reason}
        })

    async def _check_auth_and_permission(self, client_id: str, username: str, token: str, permission: str, map_name: Optional[str] = None) -> bool:
        # If the request comes from server_startup, bypass authentication and permission checks
        if client_id == "server_startup":
            self.logger.debug("Bypassing auth/permission checks for server_startup client.")
            return True

        if not username or not token:
            await self._fail(permission + "_fail", client_id, "Missing username or token.")
            return False

        if not self.user_service.is_authenticated(username, token):
            await self._fail(permission + "_fail", client_id, "Not authenticated.")
            return False

        if self.role_manager.has_permission(username, permission):
            return True

        if map_name:
            game_map = await self.map_repository.load_map(map_name)
            if not game_map:
                await self._fail(permission + "_fail", client_id, f"Map '{map_name}' not found.")
                return False

            if username in game_map.owners:
                return True
            else:
                await self._fail(permission + "_fail", client_id, "Insufficient permissions (not owner).")
                return False
        else:
            await self._fail(permission + "_fail", client_id, "No permission to perform this operation.")
            return False

    async def send_map_state_to_user(self, map_name: str, username: str):
        game_map = await self.map_repository.load_map(map_name)
        if not game_map:
            return
        user_client_id = self.user_service.get_client_id_by_username(username)
        if not user_client_id:
            return

        users_in_map = await self.user_service.user_repository.get_users_in_map(map_name)
        players_info = []
        for u in users_in_map:
            players_info.append({
                "username": u.username,
                "position": u.position,
                "yaw": u.yaw,
                "pitch": u.pitch
            })

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

        await self.event_dispatcher.dispatch("map_state", {
            "client_id": user_client_id,
            "message": map_data
        })

    async def broadcast_to_map(self, map_name: str, event_type: str, data: dict, exclude_username: Optional[str] = None):
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
        token = msg.get("token", None)  # token may be missing if coming from server_startup
        map_name = msg.get("map_name")
        map_size = msg.get("map_size")
        start_position = msg.get("start_position")
        is_public = msg.get("is_public", True)
        tiles = msg.get("tiles", {})

        if not await self._check_auth_and_permission(client_id, username, token, "create_map"):
            return

        if not map_name or not map_size or not start_position:
            await self._fail("map_create_fail", client_id, "Missing required fields.")
            return

        if await self.map_repository.map_exists(map_name):
            await self._fail("map_create_fail", client_id, f"Map '{map_name}' already exists.")
            return

        from domain.maps.tile import Tile
        new_map = Map(map_name=map_name, map_size=tuple(map_size), start_position=tuple(start_position), is_public=is_public)

        for k, tile_data in tiles.items():
            tile_position = tile_data["tile_position"]
            tile_type = tile_data["tile_type"]
            is_wall = tile_data["is_wall"]
            if not new_map.is_tile_within_bounds(tile_position):
                await self._fail("map_create_fail", client_id, f"Tile {k} is out of map bounds.")
                return
            tile = Tile(tile_type, tile_position, is_wall)
            added = await new_map.add_tile(k, tile)
            if added:
                self.logger.info(f"Added a {tile_type} tile within map bounds.")

        if username:  # If we have a username, add as owner
            await new_map.add_owner(username)
            await new_map.join_map(username)

        success = await self.map_repository.save_map(new_map)
        if success:
            self.logger.info(f"Map '{map_name}' created successfully.")
            await self._ok("map_create_ok", client_id, {"map_name": map_name})
        else:
            await self._fail("map_create_fail", client_id, "Failed to save map.")

    async def handle_map_remove_request(self, event_data):
        # Unchanged logic, except it will still require token if not server_startup client
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        token = msg.get("token")
        map_name = msg.get("map_name")

        if not map_name:
            await self._fail("map_remove_fail", client_id, "map_name is required.")
            return

        if not await self._check_auth_and_permission(client_id, username, token, "remove_map", map_name):
            return

        if not await self.map_repository.map_exists(map_name):
            await self._fail("map_remove_fail", client_id, f"Map '{map_name}' does not exist.")
            return

        success = await self.map_repository.remove_map(map_name)
        if success:
            self.logger.info(f"Map '{map_name}' removed successfully.")
            await self._ok("map_remove_ok", client_id, {"map_name": map_name})
        else:
            await self._fail("map_remove_fail", client_id, "Failed to remove map.")

    async def handle_map_tile_add_request(self, event_data):
        # Similar logic as before
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        token = msg.get("token")
        map_name = msg.get("map_name")
        tile_data = msg.get("data", {})
        tile_position = tile_data.get("tile_position")
        tile_type = tile_data.get("tile_type")
        is_wall = tile_data.get("is_wall", False)

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
            await self._ok("map_tile_add_ok", client_id, {"map_name": map_name, "tile_key": tile_key})
        else:
            await self._fail("map_tile_add_fail", client_id, "Failed to save map.")

    async def handle_map_tile_remove_request(self, event_data):
        # Similar logic as before
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        token = msg.get("token")
        map_name = msg.get("map_name")
        tile_key = msg.get("tile_key")

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
            await self._fail("map_tile_remove_fail", client_id, f"Tile '{tile_key}' does not exist.")
            return

        if await self.map_repository.save_map(game_map):
            await self._ok("map_tile_remove_ok", client_id, {"map_name": map_name, "tile_key": tile_key})
        else:
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
            await self._fail("map_zone_add_fail", client_id, "Zone key already exists.")
            return

        if await self.map_repository.save_map(game_map):
            await self._ok("map_zone_add_ok", client_id, {"map_name": map_name, "zone_key": zone_key})
        else:
            await self._fail("map_zone_add_fail", client_id, "Failed to save map.")

    async def handle_map_zone_remove_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        token = msg.get("token")
        map_name = msg.get("map_name")
        zone_key = msg.get("zone_key")

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
            await self._fail("map_zone_remove_fail", client_id, f"Zone '{zone_key}' does not exist.")
            return

        if await self.map_repository.save_map(game_map):
            await self._ok("map_zone_remove_ok", client_id, {"map_name": map_name, "zone_key": zone_key})
        else:
            await self._fail("map_zone_remove_fail", client_id, "Failed to save map.")

    async def handle_map_join_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg["username"]
        token = msg["token"]
        map_name = msg["map_name"]

        if not self.user_service.is_authenticated(username, token):
            await self._fail("map_join_fail", client_id, "Not authenticated.")
            return

        user = await self.user_service.user_repository.load_user(username)
        if not user:
            await self._fail("map_join_fail", client_id, "User not found.")
            return

        game_map = await self.map_repository.load_map(map_name)
        if not game_map:
            await self._fail("map_join_fail", client_id, f"Map '{map_name}' not found.")
            return

        start_pos = game_map.start_position
        if self.collision_manager and not await self.collision_manager.is_valid_position(map_name, start_pos):
            await self._fail("map_join_fail", client_id, "Cannot join map, start position blocked.")
            return

        user.current_map = map_name
        user.position = start_pos
        await self.user_service.user_repository.save_user(user)

        await self._ok("map_join_ok", client_id, {"map_name": map_name, "position": user.position})
        await self.send_map_state_to_user(map_name, username)

    async def handle_map_leave_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg["username"]
        token = msg["token"]

        if not self.user_service.is_authenticated(username, token):
            await self._fail("map_leave_fail", client_id, "Not authenticated.")
            return

        user = await self.user_service.user_repository.load_user(username)
        if not user:
            await self._fail("map_leave_fail", client_id, "User not found.")
            return

        if user.current_map:
            await self.broadcast_to_map(user.current_map, "player_left_map", {"username": username}, exclude_username=username)

        user.current_map = None
        user.position = (0,0,0)
        await self.user_service.user_repository.save_user(user)

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

        if not await self._check_auth_and_permission(client_id, username, token, "modify_physics", map_name):
            return

        game_map = await self.map_repository.load_map(map_name)
        if not game_map:
            await self._fail("map_physics_update_fail", client_id, f"Map '{map_name}' not found.")
            return

        if gravity is not None:
            game_map.physics.gravity = gravity
        if air_resistance is not None:
            game_map.physics.air_resistance = air_resistance
        if friction is not None:
            game_map.physics.friction = friction

        success = await self.map_repository.save_map(game_map)
        if success:
            await self._ok("map_physics_update_ok", client_id, {
                "map_name": map_name,
                "physics": game_map.physics.to_dict()
            })
            await self.broadcast_to_map(map_name, "map_physics_update", {
                "map_name": map_name,
                "physics": game_map.physics.to_dict()
            })
        else:
            await self._fail("map_physics_update_fail", client_id, "Failed to save updated physics.")
