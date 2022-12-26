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
      if data["type"] == "auth_token":
        player.auth_token = data["auth_token"]
        tts.output("authentication token received")
      if data["type"] == "menu":
        pass
      if data["type"] == "move":
        pass
      if data["type"] == "error":
        error = data["error"]
        tts.output("type error message")
      else:
        pass
#        tts.output("received data has an unknown type")
    except:
      tts.output("something went wrong")

# Function to handle user input from the keyboard
def handle_input():
  # Use the Clock object to limit the number of events that are checked per second
  clock = pygame.time.Clock()

  # Continuously check for keyboard events
  while True:
    clock.tick(60)

    for event in pygame.event.get():
      # Check if the user pressed the "x" button at the top right of the screen
      if event.type == pygame.QUIT:
        server_socket.close()
        pygame.quit()
        sys.exit()
        break

      # Check if the user pressed a key
      if event.type == pygame.KEYDOWN:
        # Check if the user pressed the left arrow key
        if event.key == pygame.K_LEFT:
          # Decrement the player's x coordinate
          player.x -= 1
          # create the move message
          message = {
          "type": "move",
          "x": player.x,
          "y": player.y,
          "z": player.z
          }
          #send the message to the server
          send_data(message)
        # Check if the user pressed the right arrow key
        if event.key == pygame.K_RIGHT:
          # Increment the player's x coordinate
          player.x += 1
          # create the move message
          message = {
          "type": "move",
          "x": player.x,
          "y": player.y,
          "z": player.z
          }
          #send the message to the server
          send_data(message)
        # Check if the user pressed the up arrow key
        if event.key == pygame.K_UP:
          # Increment the player's y coordinate
          player.y += 1
          # create the move message
          message = {
          "type": "move",
          "x": player.x,
          "y": player.y,
          "z": player.z
          }
          #send the message to the server
          send_data(message)
        # Check if the user pressed the down arrow key
        if event.key == pygame.K_DOWN:
          # decrement the player's y coordinate
          player.y -= 1
          # create the move message
          message = {
          "type": "move",
          "x": player.x,
          "y": player.y,
          "z": player.z
          }
          #send the message to the server
          send_data(message)
        # Check if the user pressed the page up key
        if event.key == pygame.K_PAGEUP:
          # Increment the player's z coordinate
          player.z += 1
          # create the move message
          message = {
          "type": "move",
          "x": player.x,
          "y": player.y,
          "z": player.z
          }
          #send the message to the server
          send_data(message)
        # Check if the user pressed the page down key
        if event.key == pygame.K_PAGEDOWN:
          # Decrement the player's z coordinate
          player.z -= 1
          # create the move message
          message = {
          "type": "move",
          "x": player.x,
          "y": player.y,
          "z": player.z
          }
          #send the message to the server
          send_data(message)
        # Check if the user pressed the "c" key
        if event.key == pygame.K_c:
          # Read out the player's current coordinates
          tts.output(f"{player.x}, {player.y}, {player.z}")
        if event.key == pygame.K_z:
          tts.output(f"{player.currentZone}")

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
        if event.key == pygame.K_DOWN:
          selection = min(selection + 1, len(options) - 1)
          speak = options[selection]
          tts.output(f"{speak}")
        if event.key == pygame.K_UP:
          selection = max(selection - 1, 0)
          speak = options[selection]
          tts.output(f"{speak}")

        if event.key == pygame.K_RETURN:
          if options[selection] == "Create account":
            pass
          if options[selection] == "Log in":
            tts.output("connecting to server")
            message = {
            "type": "login",
            "auth_token": player.auth_token,
            "username": "admin",
            "password": "admin",
            }
            server_socket.connect(("localhost", 33288))
            # Create a new thread to send data to the server
            send_data_thread = threading.Thread(target=send_data)
            send_data_thread.start()
            # Create a thread to continuously receive data from the server
            receive_data_thread = threading.Thread(target=receive_data)
            receive_data_thread.start()

            send_data(message)
            break
          if options[selection] == "Exit":
            pygame.quit()
            sys.exit()
            break
          if options[selection] == "About":
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

  # Create a thread to handle user input from the keyboard
  input_thread = threading.Thread(target=handle_input)
  input_thread.start()

  # Update the game window
  pygame.display.update()

  # Set the clock timer to 60 fps
  clock.tick(60)

# Start the game
if __name__ == '__main__':
  main()