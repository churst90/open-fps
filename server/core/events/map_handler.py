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
        self.event_dispatcher.subscribe_internal("handle_create_map", self.handle_create_map)
        self.event_dispatcher.subscribe_internal("handle_remove_map", self.handle_remove_map)
        self.event_dispatcher.subscribe_internal("handle_add_tile", self.handle_add_tile)
        self.event_dispatcher.subscribe_internal("handle_remove_tile", self.handle_remove_tile)
        self.event_dispatcher.subscribe_internal("handle_add_zone", self.handle_add_zone)
        self.event_dispatcher.subscribe_internal("handle_remove_zone", self.handle_remove_zone)

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
        username = event_data['username']  # Ensure username is part of the event data
        # Dispatch permission check event
        await self.event_dispatcher.dispatch("permission_check", {
            'username': event_data['username'],
            'map_id': event_data['map_name'],
            'permission': 'add_tile',
            'follow_up_event': 'add_tile',  # This tells the listener what action to take on success
            'scope': 'map',
            'event_data': event_data  # Pass along the original event data for use after permission check
        })

    async def handle_remove_tile(self, event_data):
        # Extract necessary information from event_data
        map_name = event_data['map_name']
        tile_key = event_data['tile_key']
        username = event_data['username']  # Ensure username is part of the event data

        # Dispatch permission check event
        await self.event_dispatcher.dispatch("permission_check", {
            'username': username,
            'permission': PERMISSION_REMOVE_TILE,
            'follow_up_event': 'remove_tile',
            'event_data': event_data,  # Include original event data for processing after permission check
            'scope': 'map',  # This action is specific to a map
            'map_id': map_name  # Assuming you can identify map by name or provide an appropriate identifier
        })

    async def handle_add_zone(self, event_data):
        # Dispatch permission check event before attempting to add the zone
        await self.event_dispatcher.dispatch("permission_check", {
            'username': event_data['username'],
            'map_id': event_data['map_name'],  # Assuming map_name is part of the event data
            'permission': 'add_zone',  # Permission needed to add a zone
            'follow_up_event': 'add_zone',  # Custom event to trigger upon successful permission check
            'scope': 'map',  # Indicating this is a map-specific action
            'event_data': event_data  # Passing the original event data for further processing upon permission grant
        })

    async def handle_remove_zone(self, event_data):
        # Similar structure to handle_remove_tile
        map_name = event_data['map_name']
        zone_key = event_data['zone_key']
        username = event_data['username']  # Ensure username is part of the event data

        # Dispatch permission check event
        await self.event_dispatcher.dispatch("permission_check", {
            'username': username,
            'permission': PERMISSION_REMOVE_ZONE,
            'follow_up_event': 'confirmed_remove_zone',
            'event_data': event_data,  # Include original event data
            'scope': 'map',  # Map-specific action
            'map_id': map_name
        })

    async def handle_create_map(self, event_data):
        # Dispatch permission check event before attempting to create the map
        await self.event_dispatcher.dispatch("permission_check", {
            'username': event_data['username'],
            'permission': 'create_map',  # Permission needed to create a map
            'follow_up_event': 'create_map',  # Custom event to trigger upon successful permission check
            'scope': 'global',  # This action isn't specific to any particular map
            'event_data': event_data  # Passing the original event data for further processing upon permission grant
        })

    async def handle_remove_map(self, map_name):
        await self.map_reg.remove_map(map_name)
