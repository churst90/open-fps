# import openal
import json
import threading
import pygame
import socket
import sys
import accessible_output2.outputs.auto
from chat import Chat
from player import Player

 # set the main variables
chats = Chat()
global_messages = []
map_messages = []
private_messages = []
player = Player("Player1")
player.logged_in=0
tts = accessible_output2.outputs.auto.Auto()
server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)

def send_data(message):
  data = message
  # Serialize the data as JSON
  data = json.dumps(data, ensure_ascii=False)

  # Encode the JSON string as a bytes object
  data = data.encode("utf-8")

  # Send the data to the server
  server_socket.sendall(data)

# Function to receive data from the server
def receive_data():

  while True:
    try:
      # Receive data from the server
      data = server_socket.recv(1024)
      # Deserialize the data from JSON format
      data = json.loads(data)
      # Check if the received data is a keep-alive message
      if data["type"] == "keep_alive":
        # Send a response to the server acknowledging the keep-alive message
        message = {"type": "keep_alive"}
        send_data(message)
      elif data["type"] == "auth_token":
        player.auth_token = data["auth_token"]
        tts.output("authentication token received")
        player.logged_in=1
      elif data["type"] == "menu":
        pass
      elif data["type"] == "move":
        tts.output(F"{data['username']} is now at position {data['x']}, {data['y']}, {data['z']}")
      elif data["type"] == "error":
        error = data["error"]
        tts.output("type error message")
      else:
        tts.output("received data has an unknown type")
    except Exception as e:
      print(e)
      print(data)
      tts.output("something went wrong" + str(e)            )
  server_socket.close()
# Function to handle user input from the keyboard
def handle_input(key):
        print("handle input")
        if key == pygame.K_LEFT:
          # Decrement the player's x coordinate
          player.x -= 1
          # create the move message
          message = {
          "type": "move",
          "username":player.username,
          "x": player.x,
          "y": player.y,
          "z": player.z
          }
          #send the message to the server
          send_data(message)
        # Check if the user pressed the right arrow key
        elif key == pygame.K_RIGHT:
          # Increment the player's x coordinate
          player.x += 1
          # create the move message
          message = {
          "type": "move",
          "username":player.username,
          "x": player.x,
          "y": player.y,
          "z": player.z
          }
          #send the message to the server
          send_data(message)
        # Check if the user pressed the up arrow key
        elif key == pygame.K_UP:
          # Increment the player's y coordinate
          player.y += 1
          # create the move message
          message = {
          "type": "move",
                    "username":player.username,
          "x": player.x,
          "y": player.y,
          "z": player.z
          }
          #send the message to the server
          send_data(message)
        # Check if the user pressed the down arrow key
        elif key == pygame.K_DOWN:
          # decrement the player's y coordinate
          player.y -= 1
          # create the move message
          message = {
          "type": "move",
          "username":player.username,
          "x": player.x,
          "y": player.y,
          "z": player.z
          }
          #send the message to the server
          send_data(message)
        # Check if the user pressed the page up key
        elif key == pygame.K_PAGEUP:
          # Increment the player's z coordinate
          player.z += 1
          # create the move message
          message = {
          "type": "move",
          "username":player.username,
          "x": player.x,
          "y": player.y,
          "z": player.z
          }
          #send the message to the server
          send_data(message)
        # Check if the user pressed the page down key
        elif key == pygame.K_PAGEDOWN:
          # Decrement the player's z coordinate
          player.z -= 1
          # create the move message
          message = {
          "type": "move",
          "username":player.username,
          "x": player.x,
          "y": player.y,
          "z": player.z
          }
          #send the message to the server
          send_data(message)
        # Check if the user pressed the "c" key
        elif key == pygame.K_c:
          # Read out the player's current coordinates
          tts.output(f"{player.x}, {player.y}, {player.z}")
        if key == pygame.K_z:
          pass

def startMenu():
  clock = pygame.time.Clock()
  tts.output("Welcome to Open Life. Please make a selection:")
  options = ["Create account", "Log in", "About", "Exit"]
  selection = 0

  while True:
    clock.tick(60)

    # define keybindings for the start menu
    for event in pygame.event.get():
      if event.type == pygame.KEYDOWN:
        if player.logged_in==1:
          handle_input(event.key)
          break
        elif event.key == pygame.K_DOWN:
          print(player.logged_in)
          selection = min(selection + 1, len(options) - 1)
          speak = options[selection]
          tts.output(f"{speak}")
          break
        elif event.key == pygame.K_UP:
          selection = max(selection - 1, 0)
          speak = options[selection]
          tts.output(f"{speak}")
          break
        elif event.key == pygame.K_RETURN:
          if options[selection] == "Create account":
            pass
          elif options[selection] == "Log in":
            tts.output("connecting to server")
            message = {
            "type": "login",
            "auth_token": player.auth_token,
            "username": "admin",
            "password": "admin",
            }
            player.username="admin"
            server_socket.connect(("localhost", 33288))
            # Create a thread to continuously receive data from the server
            receive_data_thread = threading.Thread(target=receive_data)
            receive_data_thread.start()
            send_data(message)
            break
          elif options[selection] == "Exit":
            pygame.quit()
            exit()
            break
          elif options[selection] == "About":
            tts.output("Open Life, version 0.5. Developed and maintained by Cody Hurst. Open Life is an open source first person shooter game developed with the assistance of Open AI and GPT 3. All questions and concerns should be emailed to me or ask to join my team talk server. Thanks and enjoy!")

def main():

  # Initialize Pygame and setup the main window
  def gameWindow():
    pygame.init()
    window_width = 800  
    window_height = 600
    window = pygame.display.set_mode((window_width, window_height))
    pygame.display.set_caption("Open Life version 1.0")

  gameWindow()

  # speak the welcome message
  tts.output("Welcome to open life. Please make a selection")

  # show the startup menu
  startMenu()
  # Update the game window
  pygame.display.update()

  # Set the clock timer to 60 fps
  clock.tick(60)

# Start the game
if __name__ == '__main__':
  main()