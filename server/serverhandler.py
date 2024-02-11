# Project specific imports
from map import Map
from player import Player
from userfunctions import User

class ServerHandler:

    def __init__(self, online_players, user_accounts, maps, data, key, network, chat):
        self.user_accounts = user_accounts
        self.online_players = online_players
        self.maps = maps
        self.key = key
        self.data = data
        self.network = network
        self.setup_map_listeners()
        self.chat = chat
        self.user = User(self.online_players, self.user_accounts, self.maps, self.network, self.key, self.data, self.chat)

    def setup_map_listeners(self):
        for map_name, map_instance in self.maps.items():
            map_instance.on('change', self.handle_map_change)

    async def handle_map_change(self, change_data):
        map_name = change_data.get('map_name')
        update_message = self.prepare_update_message(map_name, change_data)
        await self.network.send(update_message, self.get_clients_on_map(map_name))

    def prepare_update_message(self, map_name, change_data):
        # Here, you will process the change_data to create a suitable update message
        # This is a placeholder logic, adjust as per your game's requirements
        return {
            "type": "map_update",
            "map_name": map_name,
            "changes": change_data
        }

    def get_clients_on_map(self, map_name):
        return [self.connections.get(player)[1] for player in self.online_players if self.online_players[player].current_map == map_name]

    async def create_map(self, name, size):
        if name not in self.maps.keys():
            map_name = name
            map_size = size
            map = Map(map_name)
            map.set_size(map_size)
            self.maps[name] = map
            self.data.export(self.maps, "maps")
# await self.handle_chat({"type": "ok", "map": self.online_players[data["username"]].map, "position": self.online_players[data["username"]].get_position(), "direction": self.online_players[data["username"]].get_direction()})

    async def check_zone(self, data):
        player, client_socket = self.online_players[data["username"]]
        zone_name = player.get_zone()
        response = {"type": "zone", "zone": zone_name}
        await self.network.send(response, [client_socket])

    async def handle_chat(self, chat_data):
        sender = chat_data["sender"]
        message = chat_data["message"]
        scope = chat_data.get("scope")

        if scope == "global":
            self.chat.send_global_message(sender, message)
            # Get all client sockets
            client_sockets = [info[1] for info in self.online_players.values()]
            await self.network.send(chat_data, client_sockets)

        elif scope == "map":
            map_name = chat_data.get("map")
            self.chat.send_map_message(map_name, sender, message)
            # Get clients on specific map
            client_sockets = self.get_clients_on_map(map_name)
            await self.network.send(chat_data, client_sockets)

        elif scope == "private":
            recipient = chat_data.get("recipient")
            self.chat.send_private_message(sender, recipient, message)
            # Get recipient's client socket
            recipient_socket = self.online_players.get(recipient)[1]
            await self.network.send(chat_data, [recipient_socket])

    def configure_logger(self):
        logger = logging.getLogger(__name__)
        logger.setLevel(logging.INFO)
        # Create a stream handler and set its level
        stream_handler = logging.StreamHandler()
        stream_handler.setLevel(logging.INFO)

        # Create a formatter and add it to the stream handler
        formatter = logging.Formatter('%(asctime)s - %(levelname)s - %(message)s')
        stream_handler.setFormatter(formatter)

        # Add the stream handler to the logger
        logger.addHandler(stream_handler)

        # Configure the root logger to propagate messages to the stream handler
        logging.getLogger().addHandler(stream_handler)
        logging.getLogger().setLevel(logging.INFO)

        return logger

    def get_clients_on_map(self, map_name):
        # Assuming each player object knows its current map
        client_sockets = [self.connections[player][1] for player, player_obj in self.online_players.items() if player_obj.current_map == map_name]
        return client_sockets

    async def handle_turn(self, data, client_socket):
        username = data.get("username")
        player = self.online_players.get(username)

        if player:
            turn_response = player.turn(data)
            await self.network.send(turn_response, [client_socket])
            # send the update to other players on the same map

    async def handle_move(self, data, client_socket):
        username = data.get("username")
        player = self.online_players.get(username)

        if player:
            move_response = player.move(data)
            await self.network.send(move_response, [client_socket])
            # Optionally broadcast this change to other players