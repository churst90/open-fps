import math
import base64
from cryptography.fernet import Fernet
from map import Map
from chat import Chat
import json
from collision import Collision
from player import Player

class ClientHandler:

  def __init__(self, user_accounts, key, players, maps):
    self.receive_thread = None
    self.f = key
    self.user_accounts = user_accounts
    self.players = players
    self.maps = maps
    self.chat = Chat()
    self.messageTypes = {
    "move": self.move,
    "turn": self.turn,
    "check_zone": self.check_zone,
    "chat": self.send_chat,
    "create_account": self.create_user_account,
    "create_map": self.create_map,
    "login": self.login,
    "logout": self.logout
    }

  def turn(self, data):
    # Get the current yaw and pitch angles of the player
    player, client_socket = self.players[data["username"]]
    yaw = data["yaw"]
    pitch = data["pitch"]

    if data["value"] == "left":
      # decrement the yaw angle by 45 degrees
      yaw = (yaw-45) % 360
    if data["value"] == "right":
      # Increment the yaw angle by 45 degrees
      yaw = (yaw + 45) % 360
    if data["value"] == "up":
      # Increment the pitch angle by 45 degrees
      pitch = min(90, pitch + 45)
    if data["value"] == "down":
      # Decrement the pitch angle by 45 degrees
      pitch = max(-90, pitch - 45)

    # Convert the pitch and yaw angles from degrees to radians
    pitch = math.radians(pitch)
    yaw = math.radians(yaw)

    # Calculate the x, y, and z components of the direction vector
    dx = math.cos(pitch) * math.cos(yaw)
    dy = math.cos(pitch) * math.sin(yaw)
    dz = math.sin(pitch)

    # Update the player's direction, yaw, and pitch angles
    player.direction = (dx, dy, dz)
    player.yaw = yaw
    player.pitch = pitch

    # Construct the update message
    update_message = {
      "type": "turn",
      "username": data["username"],
      "direction": player.direction,
      "facing": self.get_cardinal_direction(player.direction),
      "yaw": yaw*180/math.pi,
      "pitch": pitch*180/math.pi
      }
    # Send the update message to all players in the same map
    self.broadcast_update(update_message, self.maps[data["map"]].players)

  def move(self, data):
    # Check if the proposed movement will collide with a wall or the edge of the map
    collision = Collision(self.maps[data["map"]])
    # Calculate the new x, y, and z values based on the direction vector and the type of move
    x = data["x"]
    y = data["y"]
    z = data["z"]
    if data["value"] == "left":
      x -= round(data["direction"][0])
      y -= round(data["direction"][1])
      z -= round(data["direction"][2])
    elif data["value"] == "right":
      x += round(data["direction"][0])
      y += round(data["direction"][1])
      z += round(data["direction"][2])
    elif data["value"] == "forward":
      x += round(data["direction"][0])
      y += round(data["direction"][1])
      z += round(data["direction"][2])
    elif data["value"] == "backward":
      x -= round(data["direction"][0])
      y -= round(data["direction"][1])
      z -= round(data["direction"][2])
    else:
      self.send_data(self.players[data["username"]][1], {"type": "error","error":"invalid move"})
      return

    if collision.check_boundary_collision(x, y, z):
      # If there is a collision, send an error message to the player
      error_message = {
      "type": "error",
      "error": "edge of the map"
      }
      self.send_data(self.players[data["username"]][1], error_message)
      return
    elif collision.check_wall_collision(x, y, z):
      error_message = {
      "type": "error",
      "error": "wall"
      }
      self.send_data(self.players[data["username"]][1], error_message)
      return

    # Update the player's position
    player, client_socket = self.players[data["username"]]
    player.x = x
    player.y = y
    player.z = z

    # Construct the update message
    update_message = {
    "type": "move",
    "username": data["username"],
    "x": x,
    "y": y,
    "z": z,
    }
    # Send the update message to all players in the same map
    self.broadcast_update(update_message, self.maps[data["map"]].players)
    return

  def login(self, data, client_socket):
    print("login function called")
    # Check if the user account exists
    if self.check_user_account(data["username"], data["password"]):
      initial_data = {
      "username": data["username"],
      "direction": (0,0,0),
      "facing": self.get_cardinal_direction((0,0,0)),
      "x": 0,
      "y": 0,
      "z": 0,
      "map": "Main",
      "zone": None
      }
      player = Player(data["username"])
      player.username = initial_data["username"]
      player.direction = initial_data["direction"]
      player.facing = initial_data["facing"]
      player.x = initial_data["x"]
      player.y = initial_data["y"]
      player.z = initial_data["z"]
      player.zone = initial_data["zone"]
      player.map = initial_data["map"]
      # Add the player object and client socket as a tuple to the players dictionary
      self.players[data["username"]] = (player, client_socket)
      print(f'{data["username"]} added to the players list')
      # add the player to the main map
      self.maps[initial_data["map"]].players[data["username"]] = self.players[data["username"]]
      print(f'{initial_data["username"]} added to the {initial_data["map"]} map')
      message = f'{data["username"]} came online'
      sender = "server"
      self.chat.send_global_message(sender, message)
      last_message = self.chat.global_messages[-1]
      sender = last_message["sender"]
      text = last_message["message"]
      print(f"{sender}: {text}")
      initial_data["type"] = "login_ok"
      client_socket.sendall(json.dumps(initial_data).encode())
      # Generate an authentication token for the client
      auth_token = self.f.encrypt(f"{data['username']}:login".encode())
      auth_token = base64.b64encode(auth_token).decode()

      # Send the authentication token to the client
      client_socket.sendall(json.dumps({"type": "auth_token", "auth_token": auth_token}).encode())
    else:
      # Send a message back to the client that the account doesn't exist
      client_socket.sendall(json.dumps({"type": "error", "message": "The account does not exist"}).encode())

  def logout(self, data, client_socket):
    print("logout method called")
    del self.players[data["username"]]
    print("player removed from the player's list")
    # Close the client socket and clean up
    client_socket.close()
    self.chat.send_global_message("Server", f'{data["username"]} has left the game')

  # Function to add a user account to the dictionary
  def create_user_account(self, username, password):
    # Encode the username and password
    encoded_username = username.encode()
    encoded_password = password.encode()

    # Encrypt the username and password
    encrypted_username = self.f.encrypt(encoded_username)
    encrypted_password = self.f.encrypt(encoded_password)

    # Add the encrypted username and password to the dictionary
    self.user_accounts[encrypted_username] = encrypted_password
    print("administrator account created and added to the user accounts")

  # Function to check if a user account exists
  def check_user_account(self, username, password):
    print("check user account method executed")
    # Encode the username and password
    encoded_username = username.encode()
    print("username encoded")
    encoded_password = password.encode()
    print("password encoded")

    # Encrypt the username and password
    encrypted_username = self.f.encrypt(encoded_username)
    print("username encrypted")
    encrypted_password = self.f.encrypt(encoded_password)
    print("password encrypted")

    # Iterate through the user accounts
    for encrypted_user, encrypted_pass in self.user_accounts.items():
      # Decrypt the encrypted username and password
      decrypted_username = self.f.decrypt(encrypted_user).decode()
      decrypted_password = self.f.decrypt(encrypted_pass).decode()

      # Compare the decrypted values to the provided values
      if decrypted_username == username and decrypted_password == password:
        # Return True if the values match
        print("values match...logging in")
        return True
      else:
        # Return False if no matching account is found
        print("values do not match...not logging in")
        return False

  def create_map(self, name, min_x, max_x, min_y, max_y, min_z, max_z):
    map = Map(name, min_x, max_x, min_y, max_y, min_z, max_z)
    self.maps[name] = map
    return map

  def listPlayers(self):
    return list(self.players.keys())

  def listMaps(self):
    return list(self.maps.keys())

  def listUsers(self):
    # Iterate over the user_accounts dictionary
    for encrypted_username, encrypted_password in self.user_accounts.items():
      # Decrypt the encrypted username and password using the Fernet instance
      decrypted_username = self.f.decrypt(encrypted_username).decode()
      decrypted_password = self.f.decrypt(encrypted_password).decode()

      # Print out the decrypted username and password
      print(f"Username: {decrypted_username}, Password: {decrypted_password}")
      
  def broadcast_update(self,message,recipients):
    for recipient in recipients.values():
      # Encode the message as a bytes object
      data = json.dumps(message).encode()
      # Send the message to the recipient using the socket's `sendall()` method
      recipient[1].sendall(data)

  def send_data(self, client_socket, message):
    # Serialize the data as JSON
    data = json.dumps(message, ensure_ascii=False)

    # Encode the JSON string as a bytes object
    data = data.encode("utf-8")

    # Send the data to the client
    client_socket.sendall(data)

  def check_zone(self, data):
    # Get the player's x, y, and z coordinates
    x = data["x"]
    y = data["y"]
    z = data["z"]

    # Get the map object for the player's current map
    map = self.maps[data["map"]]

    # Iterate over the zones in the map
    for zone in map.zones:
      # Check if the player's coordinates are within the bounds of the current zone
      if x >= zone["min_x"] and x <= zone["max_x"] and y >= zone["min_y"] and y <= zone["max_y"] and z >= zone["min_z"] and z <= zone["max_z"]:
        # If they are, return the name of the zone
        message = {
        "type": "zone",
        "zone": zone["name"]
        }
        return self.players[data["username"]][1].sendall(json.dumps(message).encode())

    # If the player is not in any of the zones, return "unknown area"
    message = {
    "type": "zone",
    "zone": "Unknown area"
    }
    return self.players[data["username"]][1].sendall(json.dumps(message).encode())

  def get_cardinal_direction(self, direction):
    # Calculate the yaw angle in degrees
    yaw = math.atan2(direction[1], direction[0])
    yaw = math.degrees(yaw)
    cardinal_direction = "north"
    # Normalize the yaw angle to a value within 360 degrees
    yaw = (yaw + 360) % 360

    # Check the yaw angle and return the appropriate cardinal direction
    if yaw >= 337.5 or yaw < 22.5:
      cardinal_direction = "north"
    elif yaw >= 22.5 and yaw < 67.5:
      cardinal_direction = "north east"
    elif yaw >= 67.5 and yaw < 112.5:
      cardinal_direction = "east"
    elif yaw >= 112.5 and yaw < 157.5:
      cardinal_direction = "south east"
    elif yaw >= 157.5 and yaw < 202.5:
      cardinal_direction = "south"
    elif yaw >= 202.5 and yaw < 247.5:
      cardinal_direction = "south west"
    elif yaw >= 247.5 and yaw < 292.5:
      cardinal_direction = "west"
    elif yaw >= 292.5 and yaw < 337.5:
      cardinal_direction = "north west"
    return cardinal_direction

  def send_chat(self, data):
    # Get the chat type and message from the data dictionary
    chat_type = data["chat"]
    message = data["message"]

    # Send the message to the appropriate message list using the Chat object
    if chat_type == "global":
        chat.send_global_message(data["username"], message)
    elif chat_type == "local":
        chat.send_map_message(data["map"], data["username"], message)
    elif chat_type == "private":
        chat.send_private_message(data["username"], data["recipient"], message)
