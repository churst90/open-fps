from include.managers.collision_manager import CollisionManager
from include.custom_logger import get_logger

class MapService:
    def __init__(self, map_registry, event_dispatcher, logger=None):
        self.map_registry = map_registry
        self.event_dispatcher = event_dispatcher
        self.logger = logger or get_logger('map_service', debug_mode=True)
        self.setup_event_subscriptions()

    def setup_event_subscriptions(self):
        """Subscribe to all necessary map-related events."""
        self.logger.debug("Setting up event subscriptions for MapService.")
        self.event_dispatcher.subscribe('map_create_request', self.create_map)
        self.event_dispatcher.subscribe('map_remove_request', self.remove_map)
        self.event_dispatcher.subscribe('map_tile_add_request', self.add_tile)
        self.event_dispatcher.subscribe('map_tile_remove_request', self.remove_tile)
        self.event_dispatcher.subscribe('map_zone_add_request', self.add_zone)
        self.event_dispatcher.subscribe('map_zone_remove_request', self.remove_zone)
        self.event_dispatcher.subscribe('user_join_map', self.join_map)
        self.event_dispatcher.subscribe('user_leave_map', self.leave_map)

    async def create_map(self, event_data):
        """Handle map creation."""
        map_name = event_data['map_name']
        self.logger.info(f"Received map creation request for map: {map_name}")
        success = await self.map_registry.create_map(event_data)
        if success:
            # Add tiles if provided
            tiles = event_data.get('tiles', [])
            for tile_data in tiles:
                await self.map_registry.add_tile(map_name, tile_data)

            self.logger.info(f"Map '{map_name}' created successfully with tiles.")
            await self.event_dispatcher.dispatch('map_create_ok', {'map_name': map_name})
        else:
            self.logger.warning(f"Failed to create map: {map_name}")
            await self.event_dispatcher.dispatch('map_create_fail', {'map_name': map_name})

    async def remove_map(self, event_data):
        """Handle map removal."""
        map_name = event_data['map_name']
        self.logger.info(f"Received request to remove map: {map_name}")
        success = await self.map_registry.remove_map(map_name)
        if success:
            self.logger.info(f"Map '{map_name}' removed successfully.")
            await self.event_dispatcher.dispatch('map_remove_ok', {'map_name': map_name})
        else:
            self.logger.warning(f"Failed to remove map: {map_name}")
            await self.event_dispatcher.dispatch('map_remove_fail', {'map_name': map_name})

    async def add_tile(self, event_data):
        """Handle tile addition."""
        map_name = event_data['map_name']
        tile_data = event_data['tile_data']
        self.logger.info(f"Received tile addition request for map: {map_name}, tile data: {tile_data}")
        success = await self.map_registry.add_tile(map_name, tile_data)
        if success:
            self.logger.info(f"Tile added to map '{map_name}' successfully.")
            await self.event_dispatcher.dispatch('map_tile_add_ok', {'map_name': map_name, 'tile_data': tile_data})
        else:
            self.logger.warning(f"Failed to add tile to map: {map_name}")
            await self.event_dispatcher.dispatch('map_tile_add_fail', {'map_name': map_name})

    async def remove_tile(self, event_data):
        """Handle tile removal."""
        map_name = event_data['map_name']
        tile_key = event_data['tile_key']
        self.logger.info(f"Received tile removal request for map: {map_name}, tile key: {tile_key}")
        success = await self.map_registry.remove_tile(map_name, tile_key)
        if success:
            self.logger.info(f"Tile removed from map '{map_name}' successfully.")
            await self.event_dispatcher.dispatch('map_tile_remove_ok', {'map_name': map_name, 'tile_key': tile_key})
        else:
            self.logger.warning(f"Failed to remove tile from map: {map_name}")
            await self.event_dispatcher.dispatch('map_tile_remove_fail', {'map_name': map_name})

    async def add_zone(self, event_data):
        """Handle zone addition."""
        map_name = event_data['map_name']
        zone_data = event_data['zone_data']
        self.logger.info(f"Received zone addition request for map: {map_name}, zone data: {zone_data}")
        success = await self.map_registry.add_zone(map_name, zone_data)
        if success:
            self.logger.info(f"Zone added to map '{map_name}' successfully.")
            await self.event_dispatcher.dispatch('map_zone_add_ok', {'map_name': map_name, 'zone_data': zone_data})
        else:
            self.logger.warning(f"Failed to add zone to map: {map_name}")
            await self.event_dispatcher.dispatch('map_zone_add_fail', {'map_name': map_name})

    async def remove_zone(self, event_data):
        """Handle zone removal."""
        map_name = event_data['map_name']
        zone_key = event_data['zone_key']
        self.logger.info(f"Received zone removal request for map: {map_name}, zone key: {zone_key}")
        success = await self.map_registry.remove_zone(map_name, zone_key)
        if success:
            self.logger.info(f"Zone removed from map '{map_name}' successfully.")
            await self.event_dispatcher.dispatch('map_zone_remove_ok', {'map_name': map_name, 'zone_key': zone_key})
        else:
            self.logger.warning(f"Failed to remove zone from map: {map_name}")
            await self.event_dispatcher.dispatch('map_zone_remove_fail', {'map_name': map_name})

    async def join_map(self, event_data):
        """Handle user joining a map."""
        username = event_data['username']
        map_name = event_data['map_name']
        self.logger.info(f"User '{username}' requested to join map: {map_name}")
        success = await self.map_registry.join_map(map_name, username)
        if success:
            self.logger.info(f"User '{username}' joined map '{map_name}' successfully.")
            await self.event_dispatcher.dispatch('map_join_ok', {'username': username, 'map_name': map_name})
        else:
            self.logger.warning(f"User '{username}' failed to join map: {map_name}")
            await self.event_dispatcher.dispatch('map_join_fail', {'username': username, 'map_name': map_name})

    async def leave_map(self, event_data):
        """Handle user leaving a map."""
        username = event_data['username']
        map_name = event_data['map_name']
        self.logger.info(f"User '{username}' requested to leave map: {map_name}")
        success = await self.map_registry.leave_map(map_name, username)
        if success:
            self.logger.info(f"User '{username}' left map '{map_name}' successfully.")
            await self.event_dispatcher.dispatch('map_leave_ok', {'username': username, 'map_name': map_name})
        else:
            self.logger.warning(f"User '{username}' failed to leave map: {map_name}")
            await self.event_dispatcher.dispatch('map_leave_fail', {'username': username, 'map_name': map_name})
