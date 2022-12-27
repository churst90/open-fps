import base64
from cryptography.fernet import Fernet
from map import Map
from chat import Chat
import json


class ClientHandler:

  def __init__(self, user_accounts, key, players, maps):
    self.f = key
    self.user_accounts = user_accounts
    self.players = players
    self.maps = maps
    self.messageTypes = {
    "move": self.move,
#    "chat": self.chat,
#    "create_account": self.create_account,
#    "create_map": self.create_map,
    "login": self.login,
    "logout": self.logout
#    "add_map_obj": self.add_obje,
#    "remove_map_obj": self.remove_obj,
#    "pvp_status": self.pvp_status,
#    "attack": self.attack
    }

  def move(self, data):
    # Update thetplayer's position on the map
    player=self.players[data["username"]][0]
    #layer["map"] = data["map"]
    player["x"] = data["x"]
    player["y"] = data["y"]
    player["z"] = data["z"]
    # Construct the update message
    update_message = {
    "type": "move",
    "username": data["username"],
    "x": data["x"],
    "y": data["y"],
    "z": data["z"],
    }
    # Get the list of players on the same map as the moving player
    recipients = self.players
    # Send the update message to the recipients
    print(update_message) 
    print(recipients)
    self.broadcast_update(update_message, recipients)
#    else:
#      logger.error("Received data has an unknown type: %s", data["type"])

  def login(self, data, client_socket):
    print("login function called")
    # Check if the user account exists
    if self.check_user_account(data["username"], data["password"]):
      # Add the player data and client socket as a tuple to the players dictionary
      self.players[data["username"]] = (data, client_socket)
      print("Player added to the players list")
#      message = "Server: user came online"
#      Chat.send_global_message(message)

      # Generate an authentication token for the client
      auth_token = self.f.encrypt(f"{data['username']}:login".encode())
      auth_token = base64.b64encode(auth_token).decode()

      # Send the authentication token to the client
      client_socket.sendall(json.dumps({"type": "auth_token", "auth_token": auth_token}).encode())
    else:
      # Send a message back to the client that the account doesn't exist
      client_socket.sendall(json.dumps({"type": "error", "message": "The account does not exist"}).encode())

  def logout(self, data, client_socket):
    # Remove the player from the list of players and send a goodbye message
    players.remove(data["username"])
    chat.send_global_message("Server", f"{data['username']} has left the game")
    # Close the client socket and clean up
    client_socket.close()
    thread.join()

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
