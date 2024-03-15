import math
from core.events.event_handler import EventHandler
from core.modules.tile import Tile
from core.modules.zone import Zone

class MapHandler(EventHandler):
    def __init__(self, map_registry, event_dispatcher):
        super().__init__(event_dispatcher)
        self.map_reg = map_registry
        self.event_dispatcher.subscribe_internal("user_registered", self.handle_user_registered)
        self.event_dispatcher.subscribe_internal("user_deregistered", self.handle_user_deregistered)

    async def handle_user_registered(self, event_data):
        username = event_data["username"]
        current_map = event_data["current_map"]
        user_instance = event_data["user_instance"]
        # Logic to add the user to the map
        map_instance = await self.map_reg.get_map_instance(current_map)
        if map_instance:
            await map_instance.add_user(username, user_instance)

    async def handle_user_deregistered(self, event_data):
        username = event_data["username"]
        current_map = event_data["current_map"]
        # Logic to remove the user from the map
        map_instance = await self.map_reg.get_map_instance(current_map)
        if map_instance:
            await map_instance.remove_user(username)

    async def handle_add_tile(self, event_data):
        map_name = event_data['map_name']
        map_instance = await self.map_reg.get_map_instance(map_name)
        if not map_instance:
            print(f"Map {map_name} not found.")
            return

        tile_data = {
            'tile_position': event_data['tile_position'],
            'tile_type': event_data['tile_type'],
            'is_wall': event_data['is_wall']
        }
        map_instance = await self.map_reg.get_map_instance(map_name)
        await map_instance.add_tile(tile_data)

    async def handle_remove_tile(self, event_data):
        map_name = event_data['map_name']
        map_instance = await self.map_reg.get_map_instance(map_name)
        if not map_instance:
            print(f"Map {map_name} not found.")
            return

        tile_data = {
        'tile_key': event_data['tile_key']
        }

        await self.map_instance.remove_tile(tile_data)

    async def handle_add_zone(self, event_data):
        map_name = event_data['map_name']
        map_instance = await self.map_reg.get_map_instance(map_name)
        if not map_instance:
            print(f"Map {map_name} not found.")
            return

        zone_data = {
            'zone_label': event_data['zone_label'],
            'zone_position': event_data['zone_position'],
            'zone_type': event_data['zone_type']
        }
        map_instance = self.map_reg.get_map_instance(map_name)
        await map_instance.add_zone(zone_data)

    async def handle_remove_zone(self, event_data):
        map_name = event_data['map_name']
        map_instance = await self.map_reg.get_map_instance(map_name)
        if not map_instance:
            print(f"Map {map_name} not found.")
            return

        zone_data = {
        'zone_key': event_data['zone_key']
        }
        map_instance = self.map_reg.get_map_instance(map_name)
        await map_instance.remove_zone(zone_data)

    async def handle_create_map(self, data):
        username = data.get("username")
        map_name = data.get("map_name")
        map_size = data.get("map_size")
        await self.map_reg.create_map(map_name, map_size)
        user_instance = self.user_reg.get_user_instance(username)
        if user_instance:
            # Update user's current map
            user_instance.current_map = map_name
            # Optionally emit an event about the map change
            self.emit_event("user_map_changed", {"username": username, "map_name": map_name})

    async def handle_remove_map(self, map_name):
        await self.map_reg.remove_map(map_name)
