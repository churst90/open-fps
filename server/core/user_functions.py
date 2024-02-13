# third party imports
import hashlib
import os

# Project specific imports
from .modules.player import Player
from .events.event_dispatcher import EventDispatcher

class User:

    def __init__(self, players, users, maps, network, key, data, chat, custom_logger):
        self.online_players = players
        self.user_accounts = users
        self.maps = maps
        self.network = network
        self.key = key
        self.data = data
        self.chat = chat
        self.logger = custom_logger

    async def turn(self, data):
        username = data["username"]
        direction = data["direction"]

        # Check if the player is online
        if username in self.online_players:
            player, client_socket = self.online_players[username]

            # Call the turn method of the player object
            player.turn(direction)

            response = {"type": "turn", "username": username, "direction": direction}
            await self.network.send(response, [(client_socket)])

    async def move(self, data):
        username = data["username"]
        move_data = data["move"]

        if username in self.online_players:
            player, client_socket = self.online_players[username]
            response = player.move(move_data)

            await self.network.send(response, [client_socket])
        else:
            response = {"type": "error", "message": "Player not found"}
            await self.network.send(response, [client_socket])

    async def login(self, data, client_socket):
        if self.check_credentials(data["username"], data["password"]):
            player = Player()
            player_dict = self.user_accounts[data["username"]]
            for key, value in player_dict.items():
                setattr(player, key, value)
                player.set_login(True)
            self.online_players[data["username"]] = (player, client_socket)

            self.maps["Main"].players[data["username"]] = (player, client_socket)

            message = {"type": "login_ok", "player_state": player.to_dict(), "map_state": self.maps[player.current_map].simplify_map("player_changed_map")}
            await self.network.send(message, [client_socket])

            message = {
                "scope": "global",
                "username": data["username"],
                "message": f'Server: {data["username"]} came online.'
            }
            await self.send_chat(message)

        else:
            await self.network.send({"type": "error", "message": "The account does not exist"}, [client_socket])

    async def logout(self, data, client_socket):
        username = data["username"]

        # Check if the user is actually online before proceeding
        if username not in self.online_players:
            self.logger.info(f"User {username} is not online.")
            return

        # Close the client socket properly
        try:
            if client_socket and not client_socket.is_closing():
                client_socket.close()
                await client_socket.wait_closed()
        except Exception as e:
            self.logger.info(f"Error closing connection for user {username}: {e}")

        # Remove the player from their current map
        current_map = self.online_players[username][0].current_map
        if current_map in self.maps and username in self.maps[current_map].players:
            del self.maps[current_map].players[username]

        # Send the global logout message
        logout_message = {
            "type": "chat",
            "scope": "global",
            "message": f"Server: {username} has logged out"
        }
        await self.network.broadcast_global_message(username, logout_message)

        # Remove the user's connection details from the Network class
        self.network.disconnect_client(username)

        # Log out the user by removing their details from the online_players dictionary
        del self.online_players[username]
        self.logger.info(f"User {username} has been logged out successfully.")
    
    async def create_user_account(self, data, client_socket):
        if data["username"] in self.user_accounts:
            self.logger.info("username already exists")
            await self.network.send({"type": "error", "message": "That chosen username already exists, please choose another one."}, [client_socket])
        else:
            self.logger.info("username does not exist. Creating it now")
            salted_hashed_password = self.hash_password(data["password"])
            new_player = Player()
            new_player.init_user(data["username"], salted_hashed_password)

            # Convert the new Player instance to a dictionary before storing
            self.logger.debug("copying the new user to the user accounts dictionary")
            self.user_accounts[data["username"]] = new_player.to_dict()

            self.logger.debug("adding the user to the online players dictionary")
            self.online_players[data["username"]] = (new_player, client_socket)

            # Place the player on the map specified in their Player object.
            current_map_name = new_player.current_map
            if current_map_name in self.maps:
                self.maps[current_map_name].players[data["username"]] = new_player
            else:
                pass

            self.logger.debug("exporting the users")
            self.data.export(self.user_accounts, "users")
            if client_socket is not None:
                await self.network.send({"type": "create_account_ok", "state": vars(new_player)}, [client_socket])
            else:
                self.logger.info(f'{data["username"]} created successfully')

    def check_credentials(self, username, password):

        if username in self.user_accounts:
            stored_password = self.user_accounts[username]["password"]
            salt = stored_password[:16]
            stored_hash = stored_password[16:]

            # Hash the input password with the stored salt
            hashed_input_password = hashlib.pbkdf2_hmac('sha256', password.encode(), salt, 100000)

            if hashed_input_password == stored_hash:
                return True
        return False

    def hash_password(self, password):
        # Create a salt and hash the password
        salt = os.urandom(16)
        hashed_password = hashlib.pbkdf2_hmac('sha256', password.encode(), salt, 100000)
        return salt + hashed_password