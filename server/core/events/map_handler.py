import math
from core.events.event_handler import EventHandler
from core.modules.tile import Tile
from core.modules.zone import Zone

class MapHandler(EventHandler):
    def __init__(self, user_registry, map_registry, event_dispatcher):
        super().__init__(event_dispatcher)
        self.user_reg = user_registry
        self.map_reg = map_registry

    async def add_tile(self, map_name, tile_position, tile_type, is_wall):
        map_instance = self.map_reg.get_map_instance(map_name)
        if not map_instance:
            print(f"Map {map_name} not found.")
            return

        new_tile = Tile(tile_position, tile_type, is_wall)
        tile_key = new_tile.tile_key  # Use the generated UUID as the key
        map_instance.tiles[tile_key] = new_tile
        map_instance.mark_changed("tiles", tile_key)

        self.map_reg.selectively_sync_map(map_name)

        self.emit_event("add_tile", {
            "tile_key": tile_key,
            "tile": new_tile.to_dict()
        })

    async def remove_tile(self, map_name, tile_key):
        map_instance = self.map_reg.get_map_instance(map_name)
        if not map_instance:
            print(f"Map {map_name} not found.")
            return

        if tile_key in map_instance.tiles:
            del map_instance.tiles[tile_key]
            map_instance.mark_changed("tiles", tile_key)

            self.map_reg.selectively_sync_map(map_name)

            self.emit_event("remove_tile", {
                "tile_key": tile_key
            })

    async def add_zone(self, map_name, zone_position, zone_type):
        map_instance = self.map_reg.get_map_instance(map_name)
        if not map_instance:
            print(f"Map {map_name} not found.")
            return

        new_zone = Zone(zone_position, zone_type)
        zone_key = new_zone.zone_key  # Use the generated UUID as the key
        map_instance.zones[zone_key] = new_zone
        map_instance.mark_changed("zones", zone_key)

        self.map_reg.selectively_sync_map(map_name)

        self.emit_event("add_zone", {
            "zone_key": zone_key,
            "zone": new_zone.to_dict()
        })

    async def remove_zone(self, map_name, zone_key):
        map_instance = self.map_reg.get_map_instance(map_name)
        if not map_instance:
            print(f"Map {map_name} not found.")
            return

        if zone_key in map_instance.zones:
            del map_instance.zones[zone_key]
            map_instance.mark_changed("zones", zone_key)

            self.map_reg.selectively_sync_map(map_name)

            self.emit_event("remove_zone", {
                "zone_key": zone_key
            })

    async def create_map(self, data):
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

    async def remove_map(self, map_name):
        await self.map_reg.remove_map(map_name)