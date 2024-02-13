from core.modules.map_manager import Map, MapRegistry

class ServerHandler:
    def __init__(self, online_players, user_accounts, maps, data, key, network, chat, event_dispatcher, custom_logger):
        self.online_players = online_players
        self.user_accounts = user_accounts
        self.maps = maps
        self.data = data
        self.key = key
        self.network = network
        self.chat = chat
        self.event_dispatcher = event_dispatcher
        self.logger = custom_logger

        # Subscribe to necessary events
        self.subscribe_to_events()

    def subscribe_to_events(self):
        # Example subscription, replace with actual event handlers
        # self.event_dispatcher.subscribe("PlayerMoved", self.handle_player_movement)
        pass

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
