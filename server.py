import sys
import json
import socket
import threading
import logging
import argparse
import base64
from cryptography.fernet import Fernet
from keymanager import KeyManager
from collision import Collision
from map import Map
from chat import Chat
from ai import AI
from items import Item
from clienthandler import ClientHandler
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)
key_manager = KeyManager("key.bin")
# Use the argparse module to parse command-line arguments
parser = argparse.ArgumentParser()
parser.add_argument("--host", type=str, default="localhost")
parser.add_argument("--port", type=int, default=33288)
args = parser.parse_args()

# Generate a key for encryption
key = Fernet.generate_key()
f = Fernet(key)

# Create an empty dictionary to store user accounts
user_accounts = {}
# Dictionary to store all created maps
maps = {}
# dictionary to store all players on the game
players = {}
client_handler = ClientHandler(user_accounts, f, players, maps)
logger.info("Open Life FPS game server, version 1.0")
logger.info("Developed and maintained by Cody Hurst, codythurst@gmail.com")
logger.info("This game was written with the assistance of Open AI's chat bot: http://chat.openai.com")
logger.info("All suggestions and comments are welcome")

# Create the "Main" map and add a grass tile and zone to it
main_map = client_handler.create_map("Main", 0, 100, 0, 100, 0, 10)
initial_tile = main_map.addTile(0, 100, 0, 100, 0, 0, "grass", False)
initial_zone = main_map.addZone("yard", 0, 100, 0, 100, 0, 0)

chat = Chat()

ai = AI("enemy", "soldier", 0, 10, 0, 10, 0, 0, 5, 1000, 100)
main_map.addAI(ai)
item = Item("credit pack", "100 credit pack", None)
main_map.addItem(item, 3, 2, 0)

# add the default administrator account
client_handler.create_user_account("admin", "admin")

# Define the broadcast message function which sends messages to those who they correspond to
def broadcast_update(message, recipients):
  for recipient in recipients:
    # Encode the message as a bytes object
    data = json.dumps(message).encode()

    # Send the message to the recipient using the socket's `sendall()` method
    recipient.sendall(data)

def verify_auth_token(token, username, action):
  print("verify auth token function executed")
  # Return True if the action is "login"
  if action == "login":
    print("the action login returned true, so skipping the verification for now...")
    return True
  print("verifying your request...")
  # Decode the base64-encoded token into a bytes object
  token = base64.b64decode(token)
  print("token decoded")
  # Decrypt the token using the Fernet instance
  message = f.decrypt(token).decode()
  print("token decrypted")
  # Split the message into the username and action
  stored_username, stored_action = message.split(":")

  # Return True if the username and action match the expected values
  return stored_username == username and stored_action == action


def receive_data(client_socket):
  while True:
    try:
      data = client_socket.recv(1024)
      data = json.loads(data)
    except:
      # If a timeout occurs, the client has not responded in time
      client_handler.logout()
      break
    for message_type, message_handler in client_handler.messageTypes.items():
        if data["type"] == message_type:
          # Call the message handler function with the data and client socket if the message type matches "login" or "logout"
          if message_type in ["login", "logout"]:
            message_handler(data, client_socket)
            # Call the message handler function with just the data if the message type is not "login" or "logout"
          else:
            message_handler(data)
          break

def main(host, port):

  # Create a TCP socket
  sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
  # Bind the socket to the given host and port
  sock.bind((host, port))
  # Listen for incoming connections
  sock.listen()
  logger.info("Listening for incoming connections on %s:%s", args.host, args.port)

  def user_input():
    # Continuously check for keyboard input to accept commands
    while True:
      command = input("\n server> ")
    
      # Check if the command is "exit"
      if command == "exit":
        # Close the socket and break out of the loop
        print("Shutting down the server...")
        sock.close()
        break
      if command == "list players":
        print(client_handler.listPlayers())
        continue
      if command == "list maps":
        print(client_handler.listMaps())
        continue
      if command == "list users":
        print(client_handler.listUsers())
        continue
      else:
        print("That command is not supported.")

  # Start a new thread to handle user input
  threading.Thread(target=user_input).start()

  # Continuously check for incoming connections in a separate thread
  while True:
    # Accept an incoming connection
    client_sock, addr = sock.accept()
    print(f"Accepted connection from {addr}")
    receive_thread = threading.Thread(target=receive_data, args=(client_sock,))
    receive_thread.start()

if __name__ == '__main__':
  main(args.host, args.port)