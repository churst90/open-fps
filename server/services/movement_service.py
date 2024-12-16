# services/movement_service.py
import logging
from typing import Optional

class MovementService:
    def __init__(self, event_dispatcher, user_repository, map_repository, collision_manager, user_service, logger: Optional[logging.Logger] = None, connection_manager=None, map_service=None):
        self.event_dispatcher = event_dispatcher
        self.user_repository = user_repository
        self.map_repository = map_repository
        self.collision_manager = collision_manager
        self.user_service = user_service
        self.logger = logger or logging.getLogger("MovementService")
        self.connection_manager = connection_manager
        self.map_service = map_service

    async def start(self):
        await self.event_dispatcher.subscribe("user_move_request", self.handle_user_move_request)
        await self.event_dispatcher.subscribe("user_turn_request", self.handle_user_turn_request)
        # If we add a user_jump_request or other movement events, subscribe similarly.

    async def handle_user_move_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        token = msg.get("token")
        direction = msg.get("direction", (0,0,0))

        if not username or not token:
            await self._fail("user_move_fail", client_id, "Missing username or token.")
            return

        if not self.user_service.is_authenticated(username, token):
            await self._fail("user_move_fail", client_id, "Not authenticated.")
            return

        user = await self.user_repository.load_user(username)
        if not user:
            await self._fail("user_move_fail", client_id, "User not found.")
            return

        old_pos = user.position
        new_x = old_pos[0] + direction[0]
        new_y = old_pos[1] + direction[1]
        new_z = old_pos[2] + direction[2]
        new_pos = (new_x, new_y, new_z)

        if not await self.collision_manager.is_valid_position(user.current_map, new_pos):
            await self._fail("user_move_fail", client_id, "Cannot move there, collision or out of bounds.")
            return

        user.position = new_pos
        await self.user_repository.save_user(user)
        await self._ok("user_move_ok", client_id, {"username": username, "position": new_pos})

        # Broadcast the player's new position to others on the same map
        if self.map_service and self.connection_manager:
            # Get all users in the map
            map_users = await self.map_service.get_users_in_map(user.current_map)
            client_ids = []
            for u in map_users:
                if u.username != username:  # exclude the mover themself if desired
                    cid = await self.connection_manager.get_client_id_by_username(u.username)
                    if cid:
                        client_ids.append(cid)
            # Dispatch an event or directly call network to inform others
            for cid in client_ids:
                await self.event_dispatcher.dispatch("player_position_update", {
                    "client_id": cid,
                    "message": {
                        "username": username,
                        "position": new_pos
                    }
                })

    async def handle_user_turn_request(self, event_data):
        msg = event_data["message"]
        client_id = event_data["client_id"]
        username = msg.get("username")
        token = msg.get("token")
        yaw_change = msg.get("yaw_change", 0)
        pitch_change = msg.get("pitch_change", 0)

        if not username or not token:
            await self._fail("user_turn_fail", client_id, "Missing username or token.")
            return

        if not self.user_service.is_authenticated(username, token):
            await self._fail("user_turn_fail", client_id, "Not authenticated.")
            return

        user = await self.user_repository.load_user(username)
        if not user:
            await self._fail("user_turn_fail", client_id, "User not found.")
            return

        user.yaw += yaw_change
        user.pitch += pitch_change
        await self.user_repository.save_user(user)
        await self._ok("user_turn_ok", client_id, {"username": username, "yaw": user.yaw, "pitch": user.pitch})

        # Optionally notify others of orientation change
        # Similar to movement, if needed:
        if self.map_service and self.connection_manager:
            map_users = await self.map_service.get_users_in_map(user.current_map)
            client_ids = []
            for u in map_users:
                if u.username != username:
                    cid = await self.connection_manager.get_client_id_by_username(u.username)
                    if cid:
                        client_ids.append(cid)
            for cid in client_ids:
                await self.event_dispatcher.dispatch("player_orientation_update", {
                    "client_id": cid,
                    "message": {
                        "username": username,
                        "yaw": user.yaw,
                        "pitch": user.pitch
                    }
                })

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
