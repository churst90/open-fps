from .modules.map_manager import MapRegistry
from .modules.user_manager import UserRegistry
from .events.user_handler import UserHandler
from .events.map_handler import MapHandler

class ServerHandler:
    def __init__(self, user_reg, map_reg, event_dispatcher, custom_logger):
        self.user_reg = user_reg
        self.map_reg = map_reg
        self.event_dispatcher = event_dispatcher
        self.map_handler = MapHandler(self.map_reg, self.event_dispatcher)
        self.user_handler = UserHandler(self.user_reg, self.map_reg, self.event_dispatcher)

    async def handle_login(self, data):
        await self.user_handler.authenticate_user(data)

    async def handle_create_account(self):
        await self.user_handler.create_account(data)

    async def handle_user_action(self, data):
        username = data.get("username")
        action_type = data.get("action_type")
        if action_type == "move":
            direction = data.get("direction")
            distance = data.get("distance")
            self.user_handler.move(username, direction, distance)
        elif action_type == "turn":
            direction = data.get("direction")
            self.user_handler.turn(username, direction)
        elif action_type == "jump":
            self.user_handler.jump(username)
        else:
            # dispatch an error message
            pass

    async def handle_map_action(self, data):
        username = data.get("username")
        action_type = data.get("action_type")
        map_name = data.get("map_name")
        if action_type == "create_map":
            map_size = data.get("map_size")
            self.map_handler.create_map(map_name, map_size)
        elif action_type == "join_map":
            self.map_handler.join_map(map_name)
        elif action_type == "remove_map":
            self.map_handler.remove_map(map_name)
        elif action_type == "add_tile":
            tile_name = data.get("tile_name")
            tile_size = data.get("tile_size")
            self.map_handler.add_tile(map_name, tile_type, tile_size)
        elif action_type == "add_zone":
            zone_name = data.get("zone_name")
            zone_size = data.get("zone_size")
            self.map_handler.add_zone(map_name, zone_name, zone_size)
        else:
            # dispatch a message for invalid map action
            pass