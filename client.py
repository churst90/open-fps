from connection import Connection
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
player = Player("player")
tts = accessible_output2.outputs.auto.Auto()
connection = Connection("localhost", 33288)

def send_data(message):
  data = message
  # Serialize the data as JSON
  data = json.dumps(data, ensure_ascii=False)

  # Encode the JSON string as a bytes object
  data = data.encode("utf-8")

  # Send the data to the server
  try:
    connection.server_socket.sendall(data)
  except:
    tts.output("Can't send data")

# Function to receive data from the server
def receive_data():

  while True:
    try:
      # Receive data from the server
      data = connection.server_socket.recv(1024)

      # Deserialize the data from JSON format
      data = json.loads(data)

      if data["type"] == "keep_alive":
        # Send a response to the server acknowledging the keep-alive message
        message = {"type": "keep_alive"}
#        send_data(message)
      elif data["type"] == "auth_token":
        player.auth_token = data["auth_token"]
        tts.output("authentication token received")
        player.logged_in=1
      elif data["type"] == "menu":
        pass
      elif data["type"] == "facing":
        tts.output(f'{data["facing"]}')
      elif data["type"] == "zone":
#        player.zone == data["zone"]
        tts.output(f'{data["zone"]}')
      elif data["type"] == "move":
        player.x = data["x"]
        player.y = data["y"]
        player.z = data["z"]
        tts.output(F"{data['username']}: {data['x']}, {data['y']}, {data['z']}")
      elif data["type"] == "login_ok":
        player.username = data["username"]
        player.map = data["map"]
        player.direction = data["direction"]
        player.zone = data["zone"]
        tts.output(f'you are on {data["map"]} at {data["x"]}, {data["y"]}, {data["z"]}')
      elif data["type"] == "error":
        error = data["error"]
        tts.output(f"{error}")
      elif data["type"] == "turn":
        player.direction = data["direction"]
        player.yaw = data["yaw"]
        player.pitch = data["pitch"]
        player.facing = data["facing"]
        tts.output(f"{player.facing}")
      else:
        tts.output("received data has an unknown type")
    except:
      player.logged_in=0
      tts.output("lost connection to the server")
      connection.disconnect()
      startMenu()
      break

def create_account():
  clock = pygame.time.Clock()
  tts.output("To create an account, enter your desired username and password and press ok. You'll be logged in automatically.")
  # Set the initial values for the username and password variables
  username = ""
  password = ""

  # Set the initial focus to the username field
  active_field = "username"

  # Set the field names for the text input fields
  field_names = {
      "username": "Username: edit",
      "password": "Password: edit",
      "create_account": "Create Account button",
      "cancel": "Cancel button"
  }

  # Set the current field name to be spoken
  current_field_name = field_names[active_field]
  tts.output(f"{current_field_name}")

  # Run the create account loop
  while True:
    clock.tick(60)
    # Handle events
    for event in pygame.event.get():
      # Quit the game when the user closes the window
      if event.type == pygame.QUIT:
        pygame.quit()
        sys.exit()
      # Handle key down events
      elif event.type == pygame.KEYDOWN:
        # Check if the user pressed the tab key
        if event.key == pygame.K_TAB:
          # Set the focus to the next field
          if active_field == "username":
            active_field = "password"
            # Set the current field name to be spoken
            current_field_name = field_names[active_field]
            tts.output(f"{current_field_name} {password}")
          elif active_field == "password":
            active_field = "create_account"
            # Set the current field name to be spoken
            current_field_name = field_names[active_field]
            tts.output(f"{current_field_name}")
          elif active_field == "create_account":
            active_field = "cancel"
            # Set the current field name to be spoken
            current_field_name = field_names[active_field]
            tts.output(f"{current_field_name}")
          elif active_field == "cancel":
            active_field = "username"
            # Set the current field name to be spoken
            current_field_name = field_names[active_field]
            tts.output(f"{current_field_name} {username}")
        # Check if the user pressed the backspace key
        elif event.key == pygame.K_BACKSPACE:
          # Delete the last character from the current field
          if active_field == "username":
            username = username[:-1]
          elif active_field == "password":
            password = password[:-1]
        # Check if the user pressed the enter key
        elif event.key == pygame.K_RETURN:
          # Check if the user is creating an account
          if active_field == "ok":
            # Send a create account request to the server
            message = {
            "type": "create_account",
            "username": username,
            "password": password
            }
            send_data(message)
          # Check if the user is canceling the create account process
          elif active_field == "cancel":
            startMenu()
        # Check if the user pressed any other key
        elif len(event.unicode) > 0:
          # Add the character to the current field
          if active_field == "username":
            username += event.unicode
          elif active_field == "password":
            password += event.unicode

def login():
  clock = pygame.time.Clock()
  tts.output("Please enter your username and password and select login")
  # Set the initial values for the username and password variables
  username = ""
  password = ""

  # Set the initial focus to the username field
  active_field = "username"

  # Set the field names for the text input fields
  field_names = {
      "username": "Username: edit",
      "password": "Password: edit",
      "login": "Login button",
      "cancel": "Cancel button"
  }
  # Set the current field name to be spoken
  current_field_name = field_names[active_field]
  tts.output(f"{current_field_name}")

  # Run the create account loop
  while True:
    clock.tick(60)
    # Handle events
    for event in pygame.event.get():
      # Quit the game when the user closes the window
      if event.type == pygame.QUIT:
        pygame.quit()
        sys.exit()
      # Handle key down events
      elif event.type == pygame.KEYDOWN:
        # Check if the user pressed the tab key
        if event.key == pygame.K_TAB:
          # Set the focus to the next field
          if active_field == "username":
            active_field = "password"
            # Set the current field name to be spoken
            current_field_name = field_names[active_field]
            tts.output(f"{current_field_name} {password}")
          elif active_field == "password":
            active_field = "login"
            # Set the current field name to be spoken
            current_field_name = field_names[active_field]
            tts.output(f"{current_field_name}")
          elif active_field == "login":
            active_field = "cancel"
            # Set the current field name to be spoken
            current_field_name = field_names[active_field]
            tts.output(f"{current_field_name}")
          elif active_field == "cancel":
            active_field = "username"
            # Set the current field name to be spoken
            current_field_name = field_names[active_field]
            tts.output(f"{current_field_name} {username}")
        # Check if the user pressed the backspace key
        elif event.key == pygame.K_BACKSPACE:
          # Delete the last character from the current field
          if active_field == "username":
            username = username[:-1]
          elif active_field == "password":
            password = password[:-1]
        # Check if the user pressed the enter key
        elif event.key == pygame.K_RETURN:
          # Check if the user is logging in
          if active_field == "login":
            message = {
            "type": "login",
            "auth_token": player.auth_token,
            "username": username,
            "password": password
            }
            tts.output("trying to connect to the server")
            try:
              # Send a login request to the server
              connection.connect()
              send_data(message)
              # Create a thread to continuously receive data from the server
              receive_data_thread = threading.Thread(target=receive_data)
              receive_data_thread.start()
              return
            except socket.error:
              tts.output("Server is down. Can't log in right now.")
              connection.disconnect()
          # Check if the user is canceling the login process
          elif active_field == "cancel":
            startMenu()
        # Check if the user pressed any other key
        elif len(event.unicode) > 0:
          # Add the character to the current field
          if active_field == "username":
            username += event.unicode
          elif active_field == "password":
            password += event.unicode

# Function to handle user input from the keyboard
def handle_input():
  clock = pygame.time.Clock()
  print("handle input")
  while True:
    clock.tick(60)
    for event in pygame.event.get():
      if event.type == pygame.QUIT:
        message = {
        "type": "logout",
        "username": player.username
        }
        send_data(message)
        connection.disconnect()
        pygame.quit()
        sys.exit()
        break
      # Handle key down events
      elif event.type == pygame.KEYDOWN:
        if event.key == pygame.K_LEFT:
          # send a move left message to the server
          send_data(player.move("left"))
        # Check if the user pressed the right arrow key
        elif event.key == pygame.K_RIGHT:
          # send a move rightt message to the server
          send_data(player.move("right"))
        # Check if the user pressed the up arrow key
        elif event.key == pygame.K_UP:
          # send a move forward message to the server
          send_data(player.move("forward"))
          # create the move message
        # Check if the user pressed the down arrow key
        elif event.key == pygame.K_DOWN:
          # send a move backward message to the server
          send_data(player.move("backward"))
        # Check if the user pressed the page up key
        elif event.key == pygame.K_PAGEUP:
          # send a move up message to the server
          send_data(          player.move("up"))
        # Check if the user pressed the page down key
        elif event.key == pygame.K_PAGEDOWN:
          # send a move down message to the server
          send_data(player.move("down"))
        # Check if the user pressed the "c" key
        elif event.key == pygame.K_c:
          # Read out the player's current coordinates
          tts.output(f"{player.x}, {player.y}, {player.z}")
        elif event.key == pygame.K_e:
          send_data(player.turn("right"))
        elif event.key == pygame.K_q:
          send_data(player.turn("left"))
        elif event.key == pygame.K_z:
          message = {
          "type": "check_zone",
          "username": player.username,
          "x": player.x,
          "y": player.y,
          "z": player.z,
          "map": player.map
          }
          send_data(message)
        elif event.key == pygame.K_h:
          tts.output(f"{player.health} health")
        elif event.key == pygame.K_j:
          tts.output(f"{player.energy} energy")
        elif event.key == pygame.K_f:
          tts.output(f"{player.facing}")

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
          handle_input()
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
            create_account()
          elif options[selection] == "Log in":
            login()
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
    game_name = "Open FPS"
    game_version = 0.0
    window_width = 800  
    window_height = 600
    window = pygame.display.set_mode((window_width, window_height))
    pygame.display.set_caption(f"{game_name} {game_version}")

  gameWindow()

  # show the startup menu
  startMenu()

  # Update the game window
  pygame.display.update()

  # Set the clock timer to 60 fps
  clock.tick(60)

# Start the game
if __name__ == '__main__':
  main()