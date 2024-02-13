import asyncio

class EventDispatcher:
    def __init__(self, network, maps):
        self.network = network
        self.maps = maps
        # Adjusting for map-wide subscriptions
        self.listeners = {map_name: {} for map_name in maps.keys()}

    def subscribe(self, map_name, event_type, listener):
        if map_name not in self.listeners:
            self.listeners[map_name] = {}
        if event_type not in self.listeners[map_name]:
            self.listeners[map_name][event_type] = []
        self.listeners[map_name][event_type].append(listener)

    async def dispatch(self, map_name, event_type, data):
        # Broadcast to all listeners of the specific map and event type
        for listener in self.listeners.get(map_name, {}).get(event_type, []):
            await listener.handle_event(event_type, data)

    async def broadcast_to_map(self, map_name, event_data):
        message = json.dumps(event_data)
        disconnected_players = []
        for player_id, player in self.maps[map_name].players.items():
            try:
                if player.network.writer is not None:
                    await self.network.send(message, player.network.writer)
            except Exception as e:
                print(f"Error sending message to player {player_id}: {e}")
                disconnected_players.append(player_id)
        # Optionally handle disconnected players
        for player_id in disconnected_players:
            # Remove from map, notify others, etc.
            pass
