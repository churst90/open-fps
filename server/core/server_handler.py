class ServerHandler:
    def __init__(self, player_reg, map_reg, network, event_dispatcher, custom_logger):
        self.player_reg = player_reg
        self.players = player_reg._users
        self.map_reg = map_reg
        self.network = network
        self.event_dispatcher = event_dispatcher
        self.logger = custom_logger

    async def move(self, data, client_socket):
        # Assuming data contains {"username": "player1", "position": "forward", "speed": 1}
        username = data['username']
        direction = data['direction']
        speed = data['speed']

        # Dispatch the event to the UserActions handler
        await self.event_dispatcher.dispatch("move", {"username": username, "direction": position, "speed": speed})

    def create_map(self, name, size):
        if name not in self.maps:
            new_map = Map(name)
            new_map.set_size(size)
            self.maps[name] = new_map
            self.data.export(self.maps, "maps")
            self.logger.info(f"Map {name} created with size {size}.")
            # Emit an event indicating a new map has been created
            self.event_dispatcher.dispatch("MapCreated", {"map_name": name, "size": size})

    def get_clients_on_map(self, map_name):
        return [socket for player, socket in self.online_players.values() if self.online_players[player].current_map == map_name]

    # Additional methods to implement event handling, if needed

# Event handlers for player movement, turn, and chat should be implemented in separate modules/classes
# and subscribed to the EventDispatcher instance passed to ServerHandler.
