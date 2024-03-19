class MapHandler:
    def __init__(self, event_dispatcher, map_service):
        self.event_dispatcher = event_dispatcher
        self.map_service = map_service

    def setup_subscriptions(self):
        self.event_dispatcher.subscribe_internal("map_create_request", self.handle_create_map)
        self.event_dispatcher.subscribe_internal("map_remove_request", self.handle_remove_map)
        self.event_dispatcher.subscribe_internal("map_tile_add", self.handle_add_tile)
        self.event_dispatcher.subscribe_internal("map_tile_remove", self.handle_remove_tile)
        self.event_dispatcher.subscribe_internal("map_zone_add", self.handle_add_zone)
        self.event_dispatcher.subscribe_internal("map_zone_remove", self.handle_remove_zone)
        self.event_dispatcher.subscribe_internal("user_join_map", self.handle_join_map)
        self.event_dispatcher.subscribe_internal("user_leave_map", self.handle_leave_map)
        self.event_dispatcher.subscribe_internal("user_account_create_ok", self.send_initial_data)

    async def handle_add_tile(self, event_data):
        username = event_data['username']
        map_name = event_data['map_name']
        tile_data = event_data['tile_data']

        try:
            # Call the map_service's add_tile method directly with the necessary parameters
            success = await self.map_service.add_tile(username, map_name, tile_data)
            
            if success:
                # If adding the tile was successful, notify relevant parties (e.g., the user or other systems)
                await self.event_dispatcher.dispatch("map_tile_add_ok", {
                    'message_type': "tile_add_ok",
                    'map_name': map_name,
                    'tile_data': tile_data,
                    'username': username
                })
            else:
                # Handle the failure to add a tile (e.g., permission denied, invalid map, etc.)
                await self.event_dispatcher.dispatch("map_tile_add_fail", {
                    'message_type': "tile_add_fail",
                    'map_name': map_name,
                    'username': username,
                }, scope = "private", recipient = username)
        except PermissionError as pe:
            # Handle permission errors specifically
            await self.event_dispatcher.dispatch("map_tile_add_fail", {
                'message_type': "tile_add_fail",
                'map_name': map_name,
                'username': username,
                'message': str(pe)
            }, scope = "private", recipient = username)

    async def handle_remove_tile(self, event_data):
        username = event_data['username']
        map_name = event_data['map_name']
        tile_key = event_data['tile_key']

        try:
            # Call the map_service's remove_tile method directly with the necessary parameters
            success = await self.map_service.remove_tile(username, map_name, tile_key)
            
            if success:
                # If removeing the tile was successful, notify relevant parties (e.g., the user or other systems)
                await self.event_dispatcher.dispatch("map_tile_remove_ok", {
                    'message_type': "tile_remove_ok",
                    'map_name': map_name,
                    'username': username
                })
            else:
                # Handle the failure to remove a tile (e.g., permission denied, invalid map, etc.)
                await self.event_dispatcher.dispatch("map_tile_remove_fail", {
                    'message_type': "tile_remove_fail",
                    'map_name': map_name,
                    'username': username,
                }, scope = "private", recipient = username)
        except PermissionError as pe:
            # Handle permission errors specifically
            await self.event_dispatcher.dispatch("map_tile_remove_fail", {
                'message_type': "tile_remove_fail",
                'map_name': map_name,
                'username': username,
                'message': str(pe)
            }, scope = "private", recipient = username)

    async def handle_add_zone(self, event_data):
        username = event_data['username']
        map_name = event_data['map_name']
        zone_data = event_data['zone_data']

        try:
            # Call the map_service's add_zone method directly with the necessary parameters
            success = await self.map_service.add_zone(username, map_name, zone_data)
            
            if success:
                # If adding the zone was successful, notify relevant parties (e.g., the user or other systems)
                await self.event_dispatcher.dispatch("map_zone_add_ok", {
                    'message_type': "zone_add_ok",
                    'map_name': map_name,
                    'zone_data': zone_data,
                    'username': username
                })
            else:
                # Handle the failure to add a zone (e.g., permission denied, invalid map, etc.)
                await self.event_dispatcher.dispatch("map_zone_add_fail", {
                    'message_type': "zone_add_fail",
                    'map_name': map_name,
                    'username': username,
                }, scope = "private", recipient = username)
        except PermissionError as pe:
            # Handle permission errors specifically
            await self.event_dispatcher.dispatch("map_zone_add_fail", {
                'message_type': "zone_add_fail",
                'map_name': map_name,
                'username': username,
                'message': str(pe)
            }, scope = "private", recipient = username)

    async def handle_remove_zone(self, event_data):
        username = event_data['username']
        map_name = event_data['map_name']
        zone_key = event_data['tile_key']

        try:
            # Call the map_service's remove_zone method directly with the necessary parameters
            success = await self.map_service.remove_zone(username, map_name, zone_key)
            
            if success:
                # If removing the zone was successful, notify relevant parties (e.g., the user or other systems)
                await self.event_dispatcher.dispatch("map_zone_remove_ok", {
                    'message_type': "zone_remove_ok",
                    'map_name': map_name,
                    'username': username
                })
            else:
                # Handle the failure to add a zone (e.g., permission denied, invalid map, etc.)
                await self.event_dispatcher.dispatch("map_zone_remove_fail", {
                    'message_type': "zone_remove_fail",
                    'map_name': map_name,
                    'username': username,
                }, scope = "private", recipient = username)
        except PermissionError as pe:
            # Handle permission errors specifically
            await self.event_dispatcher.dispatch("map_zone_remove_fail", {
                'message_type': "zone_remove_fail",
                'map_name': map_name,
                'username': username,
                'message': str(pe)
            }, scope = "private", recipient = username)

    async def handle_create_map(self, event_data):
        username = event_data['username']
        map_data = {
            'map_name': event_data['map_name'],
            'map_size': event_data['map_size'],
            'start_position': event_data['start_position']
        }

        try:
            success = await self.map_service.create_map(username, map_data)

            if success:
                await self.event_dispatcher.dispatch("map_create_ok", {
                    'message_type': "map_create_ok",
                    'map_name': map_data['map_name'],
                    'username': username
                })
            else:
                await self.event_dispatcher.dispatch("map_create_fail", {
                    'message_type': "map_create_fail",
                    'map_name': map_data['map_name'],
                    'username': username,
                    'message': 'Failed to create map. Please check map details or permissions.'
                }, scope="private", recipient=username)
        except Exception as e:
            await self.event_dispatcher.dispatch("map_create_fail", {
                'message_type': "map_create_fail",
                'map_name': map_data['map_name'],
                'username': username,
                'message': str(e)
            }, scope="private", recipient=username)

    async def handle_remove_map(self, event_data):
        username = event_data['username']
        map_name = event_data['map_name']

        try:
            success = await self.map_service.remove_map(map_name, username)

            if success:
                await self.event_dispatcher.dispatch("map_remove_ok", {
                    'message_type': "map_remove_ok",
                    'map_name': map_name,
                    'username': username
                })
            else:
                await self.event_dispatcher.dispatch("map_remove_fail", {
                    'message_type': "map_remove_fail",
                    'map_name': map_name,
                    'username': username,
                    'message': 'Failed to remove map. Please check map details or permissions.'
                }, scope="private", recipient=username)
        except Exception as e:
            await self.event_dispatcher.dispatch("map_remove_fail", {
                'message_type': "map_remove_fail",
                'map_name': map_name,
                'username': username,
                'message': str(e)
            }, scope="private", recipient=username)

    async def handle_join_map(self, event_data):
        username = event_data['username']
        map_name = event_data['map_name']

        try:
            success = await self.map_service.join_map(username, map_name, event_data)

            if success:
                await self.event_dispatcher.dispatch("map_join_ok", {
                    'message_type': "map_join_ok",
                    'map_name': map_name,
                    'username': username
                })
            else:
                await self.event_dispatcher.dispatch("map_join_fail", {
                    'message_type': "map_join_fail",
                    'map_name': map_name,
                    'username': username,
                    'message': 'Failed to join map. Please check map details or permissions.'
                }, scope="private", recipient=username)
        except Exception as e:
            await self.event_dispatcher.dispatch("map_join_fail", {
                'message_type': "map_join_fail",
                'map_name': map_name,
                'username': username,
                'message': str(e)
            }, scope="private", recipient=username)

    async def handle_leave_map(self, event_data):
        username = event_data['username']
        map_name = event_data['map_name']

        try:
            success = await self.map_service.leave_map(username, map_name, event_data)

            if success:
                await self.event_dispatcher.dispatch("map_leave_ok", {
                    'message_type': "map_leave_ok",
                    'map_name': map_name,
                    'username': username
                })
            else:
                await self.event_dispatcher.dispatch("map_leave_fail", {
                    'message_type': "map_leave_fail",
                    'map_name': map_name,
                    'username': username,
                    'message': 'Failed to leave map. Please check map details or permissions.'
                }, scope="private", recipient=username)
        except Exception as e:
            await self.event_dispatcher.dispatch("map_leave_fail", {
                'message_type': "map_leave_fail",
                'map_name': map_name,
                'username': username,
                'message': str(e)
            }, scope="private", recipient=username)
