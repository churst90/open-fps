# Standard library imports
import argparse
import pickle
from cryptography.fernet import Fernet
import base64
import os

# Third party library imports
import asyncio
import logging

# Disable asyncio debug mode
os.environ["PYTHONASYNCIODEBUG"] = "0"

# Project specific imports
from securitymanager import SecurityManager as sm
from data import Data
from map import Map
from collision import Collision
from player import Player
from ai import AI
from items import Item

class Server:

    async def accept_connections(self):
        print("accept connections method called")
        self.server = await asyncio.start_server(self.handle_client, self.host, self.port)
        print(f"Listening for incoming connections on {host}:{port}")

#        async with self.server:
        await self.server.start_serving()

    async def handle_client(self, reader, writer):
        addr = writer.get_extra_info("peername")
        client_socket = writer.get_extra_info("socket")

        print(f"Accepted connection from {addr}")
        while True:
            try:
                data = await reader.read(1024)
                if not data:
                    break
                data = pickle.loads(data)
                print("Adding data to message queue:", data)
                await self.message_queue.put((data, client_socket))
                await self.process_received_data()
                print("Message queue size:", self.message_queue.qsize())

            except asyncio.CancelledError:
                break

        print(f"Connection closed: {addr}")

    async def send(self, data, recipients):
        serialized_data = pickle.dumps(data)

        for recipient in recipients:
            recipient[0].write(serialized_data)
            await asyncio.sleep(0)  # Allow other tasks to run during the sending process

    async def process_received_data(self):
        print("process received data method called")
        print("Message queue size:", self.message_queue.qsize())

        while not self.message_queue.empty():
            print("while loop executed")
            data, client_socket = await self.message_queue.get()
            print("Received data:", data)

            message_type = data["type"]
            if message_type in self.messageTypes:
                message_handler = self.messageTypes[message_type]
                if message_type in ["create_account", "login", "logout"]:
                    await message_handler(data, client_socket)
                    print("Calling message handler:", message_type)
                else:
                    message_handler(data)
            else:
                await self.send({"type": "error", "message": "Invalid message type"}, [client_socket, None])

    async def close(self):
        if self.server:
            self.server.close()
            await self.server.wait_closed()
            self.server = None

    async def turn(self, data):
        username = data["username"]
        direction = data["direction"]

        # Check if the player is online
        if username in self.online_players:
            player, client_socket = self.online_players[username]

            # Call the turn method of the player object
            player.turn(direction)

            response = {"type": "turn", "username": username, "direction": direction}
            await self.send(response, [(client_socket, None)])

    async def move(self, data):
        username = data["username"]
        move_data = data["move"]

        if username in self.online_players:
            player, client_socket = self.online_players[username]
            response = player.move(move_data)

            await self.send(response, [(client_socket, None)])
        else:
            response = {"type": "error", "message": "Player not found"}
            await self.send(response, [(client_socket, None)])

    async def login(self, data, client_socket):
        print("login method executed")
        if self.check_credentials(data["username"], data["password"]):
            print("user authorized. creating the user object")
            player = Player(data["username"])
            player_data = self.user_accounts[data["username"]]
            for field, value in player_data.items():
                setattr(player, field, value)
            self.online_players[data["username"]] = (player, client_socket)

            message = {"type": "ok", "direction": player.direction, "position": player.position, "pitch": player.pitch, "yaw": player.yaw, "map": player.map, "zone": player.zone, "health": player.health, "energy": player.energy}
            await self.send(message, [client_socket, None])

            message = {
                "scope": "global",
                "username": data["username"],
                "message": f'Server: {data["username"]} came online.'
            }
            await self.send_chat(message)

        else:
            await self.send({"type": "error", "message": "The account does not exist"}, [(client_socket, None)])

    async def logout(self, data, client_socket):
        print("logout method called")
        await client_socket.close()
        del self.online_players[data["username"]]
        print("player removed from the player's list")
        await self.send_chat({"type": "chat", "scope": "global", "message": f'Server: {data["username"]} has logged out'})

    async def create_user_account(self, data, client_socket):
        if data["username"] in self.user_accounts:
            print("username already exists")
            await self.send({"type": "error", "message": "That chosen username already exists, please choose another one."}, [client_socket, None])
        else:
            print("username does not exist. Creating it now")
            encrypted_password = self.encrypt_password(data["password"])
            print("password encrypted")
            initial_data = {"username": data["username"], "password": encrypted_password, "position": [0, 0, 0], "map": self.maps["Main"], "direction": [0, 1, 0], "pitch": 0, "yaw": 0, "zone": "None"}
            print("creating the player object")
            player = Player()
            print("setting the object attributes equal to the stored values")
            for field, value in initial_data.items():
                setattr(player, field, value)
            print("adding the user to the online players dictionary")
            self.online_players[data["username"]] = (player, client_socket)
            print("copying the new user to the user accounts dictionary")
            self.user_accounts[data["username"]] = player
            print("exporting the users")
            self.data.export(self.user_accounts, "users")
            if client_socket is not None:
                await self.send({"type": "ok", "state": player_data}, [client_socket, None])
            else:
                print(f'{data["username"]} created successfully')

    def check_credentials(self, username, password):
        print("check credentials method executed")
        # Check if the username exists in the user accounts
        if username in self.user_accounts:
            print("username matches")
            stored_password = self.user_accounts[username]["password"]
            decrypted_stored_password = self.decrypt_password(stored_password)

            if decrypted_stored_password == password:
                print("Passwords match, continuing...")
                return True
        return False

    async def create_map(self, name, size):
        if name not in self.maps.keys():
            map_name = name
            map_size = size
            map = Map(map_name)
            map.set_size(map_size)
            self.maps[name] = map
# await self.send_chat({"type": "ok", "map": self.online_players[data["username"]].map, "position": self.online_players[data["username"]].get_position(), "direction": self.online_players[data["username"]].get_direction()})

    def check_zone(self, data):
        player, client_socket = self.online_players[data["username"]]
        zone_name = player.get_zone()
        response = {"type": "zone", "zone": zone_name}
        self.send(response, [client_socket, None])

    def send_chat(self, data):
        scope = data["scope"]
        if scope == "global":
            message = {"type": "chat", "scope": "global", "message": data["message"]}
            destination_socket_list = self.dict_to_list(self.online_players, 1)
            self.send(message, destination_socket_list)
        elif scope == "map":
            map_name = data["map"]
            message = {"type": "chat", "scope": "map", "message": data["message"]}
            destination_socket_list = self.dict_to_list(self.maps[map_name].players, 1)
            self.send(message, destination_socket_list)
        elif scope == "private":
            recipient = data["recipient"]
            message = {"type": "chat", "scope": "private", "message": data["message"]}
            destination_socket_list = self.dict_to_list(self.online_players[recipient], 1)
            self.send(message, destination_socket_list)

    def decrypt_password(self, encrypted_password):
        decrypted_password = self.key.decrypt(encrypted_password.encode()).decode()
        return decrypted_password

    def encrypt_password(self, password):
        encrypted_password = self.key.encrypt(password.encode()).decode()
        return encrypted_password

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

    def dict_to_list(self, src_dict, value_index):
        destination_list = []
        for i in src_dict.values():
            destination_list.append(i[value_index])
        return destination_list

    def __init__(self, host, port):
        self.host = host
        self.port = port
        self.sm = sm("security.key")
#        self.sm.generate_key()
#        self.sm.save_key()
        self.sm.load_key()
        self.key = self.sm.get_key()
        self.data = Data(self.key)
        self.logger = self.configure_logger()
        self.listen_task = None
        self.user_input_task = None
        self.shutdown_event = asyncio.Event()
        self.online_players = {}
        self.user_accounts = {}
        self.message_queue = asyncio.Queue()
        self.messageTypes = {"move": self.move, "turn": self.turn, "check_zone": self.check_zone, "chat": self.send_chat, "create_account": self.create_user_account, "create_map": self.create_map, "login": self.login, "logout": self.logout}

    async def start(self):
        await self.initialize()

        self.server = await asyncio.start_server(
            self.handle_client, host=self.host, port=self.port
        )
        print(f"Listening for incoming connections on {self.host}:{self.port}")

        loop = asyncio.get_event_loop()
        self.listen_task = loop.create_task(self.server.serve_forever())
        self.user_input_task = loop.create_task(self.user_input())

        try:
            await asyncio.gather(self.listen_task, self.user_input_task, self.shutdown_event.wait())
        except asyncio.CancelledError:
            pass

    async def listen_for_connections(self):
        await self.accept_connections()

    async def initialize(self):
        print("Open Life FPS game server, version 1.0")
        print("Developed and maintained by Cody Hurst, codythurst@gmail.com")
        print("This game was written with the assistance of Open AI's chat bot: http://chat.openai.com")
        print("All suggestions and comments are welcome")

        print("Loading maps database")
        try:
            self.maps = self.data.load("maps")
            if self.maps == {}:
                print("No maps found. Creating the default map")
                await self.create_map("Main", (0, 100, 0, 100, 0, 10))
# map creation code here
                self.data.export(self.maps, "maps")
                print("Maps exported successfully.")
            else:
                print("Maps loaded successfully")
        except TypeError:
            raise TypeError("The maps variable is not serializable. It has a data type of " + str(type(self.maps)))

        print("Loading users database...")
        try:
            self.user_accounts = self.data.load("users")
            if self.user_accounts == {}:
                print("No user database found. Creating the default admin account...")
                await self.create_user_account({"username": "admin", "password": "admin"}, None)
                print("admin created successfully")
                self.user_accounts = self.data.load("users")
                print("Loaded users into memory")
            else:
                print("Users loaded successfully")
        except TypeError:
            raise TypeError("The users variable is not serializable. It has a data type of " + str(type(self.user_accounts)))

    async def shutdown(self):
        print("Shutting down the server...")

        if self.listen_task:
            self.listen_task.cancel()

        if self.user_input_task:
            self.user_input_task.cancel()

        await self.server.wait_closed()
        self.shutdown_event.set()

    async def exit(self):
        print("Disconnecting all players...")
        if isinstance(self.online_players, dict):
            for data in self.online_players.values():
                data[1].close()
        print("Removing all players from the players list...")
        self.online_players.clear()
        await self.shutdown()

    async def user_input(self):
        print("user input method executed")
        loop = asyncio.get_event_loop()
        while True:
            command = await loop.run_in_executor(None, input, "\nserver> ")
            if command == "exit":
                await self.exit()
                break
            elif command == "list players":
                print(self.online_players)
            elif command == "list maps":
                if self.maps:
                    for mapname in self.maps.keys():
                        print(mapname)
                else:
                    print("No maps found.")
            elif command == "list users":
                if self.user_accounts:
                    for username in self.user_accounts.keys():
                        print(username)
                else:
                    print("No user accounts found.")
            elif command == "list items":
                try:
                    print(self.maps)
                except:
                    print("Couldn't load the dictionary")
            else:
                print("That command is not supported.")

async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", type=str, default="localhost")
    parser.add_argument("--port", type=int, default=33288)
    args = parser.parse_args()

    server_instance = Server(args.host, args.port)
    
    loop = asyncio.get_event_loop()

    try:
        await server_instance.start()
    except KeyboardInterrupt:
        await server_instance.exit()
#    finally:
#        loop.close()

if __name__ == '__main__':
    asyncio.run(main())