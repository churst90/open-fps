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
      elif data["type"] == "turn":
        player.direction = data["direction"]
        tts.output(f"{player.direction} degrees")
      else:
        tts.output("received data has an unknown type")
    except Exception as e:
      print(e)
      print(data)
      tts.output("something went wrong" + str(e)            )
  server_socket.close()

def create_account():
  # Set the window size and title
  window = pygame.display.set_mode((300, 200))
  pygame.display.set_caption("Create Account")

  # Set the font and font size for the text input fields
  font = pygame.font.Font(None, 32)

  # Create the text input fields
  username_field = pygame.Rect(10, 10, 280, 32)
  password_field = pygame.Rect(10, 50, 280, 32)
  ok_button = pygame.Rect(10, 90, 100, 32)
  cancel_button = pygame.Rect(180, 90, 100, 32)

  # Set the initial focus to the username field
  active_field = "username"

  # Set the initial values for the username and password variables
  username = "Player"
  password = "password"

  # Set the field names for the text input fields
  field_names = {
      "username": "Username: edit",
      "password": "Password: edit",
      "ok": "OK button",
      "cancel": "Cancel button"
  }

  # Set the current field name to be spoken
  current_field_name = field_names[active_field]
  tts.output(f"{current_field_name}")

  # Run the game loop
  while True:
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
            active_field = "ok"
            # Set the current field name to be spoken
            current_field_name = field_names[active_field]
            tts.output(f"{current_field_name}")
          elif active_field == "ok":
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

# Clear the window
  window.fill((0, 0, 0))

  # Render the text input fields
  pygame.draw.rect(window, (255, 255, 255), username_field)
  pygame.draw.rect(window, (255, 255, 255), password_field)

  # Render the buttons
  pygame.draw.rect(window, (0, 255, 0), ok_button)
  pygame.draw.rect(window, (255, 0, 0), cancel_button)

  # Render the text for the text input fields and buttons
  username_text = font.render(username, True, (0, 0, 0))
  password_text = font.render(password, True, (0, 0, 0))
  ok_text = font.render(field_names["ok"], True, (255, 255, 255))
  cancel_text = font.render(field_names["cancel"], True, (255, 255, 255))

  # Blit the text to the window
  window.blit(username_text, (15, 15))
  window.blit(password_text, (15, 55))
  window.blit(ok_text, (15, 95))
  window.blit(cancel_text, (185, 95))

  # Update the window
  pygame.display.update()

def login():
  tts.output("Logging in...")
  message = {
  "type": "login",
  "auth_token": player.auth_token,
  "username": "admin",
  "password": "admin",
  }
  player.username="admin"
  try:
    server_socket.connect(("localhost", 33288))
  except socket.error:
    tts.output("Server is unavailable. Retrying...")
    login()
  # Create a thread to continuously receive data from the server
  receive_data_thread = threading.Thread(target=receive_data)
  receive_data_thread.start()
  send_data(message)

# Function to handle user input from the keyboard
def handle_input(key):
  print("handle input")
  if key == pygame.K_LEFT:
    # send a move left message to the server
    send_data(player.move("left"))
  # Check if the user pressed the right arrow key
  elif key == pygame.K_RIGHT:
    # send a move rightt message to the server
    send_data(player.move("right"))
  # Check if the user pressed the up arrow key
  elif key == pygame.K_UP:
    # send a move forward message to the server
    send_data(player.move("forward"))
    # create the move message
  # Check if the user pressed the down arrow key
  elif key == pygame.K_DOWN:
    # send a move backward message to the server
    send_data(player.move("backward"))
  # Check if the user pressed the page up key
  elif key == pygame.K_PAGEUP:
    # send a move up message to the server
    send_data(          player.move("up"))
  # Check if the user pressed the page down key
  elif key == pygame.K_PAGEDOWN:
    # send a move down message to the server
    send_data(player.move("down"))
  # Check if the user pressed the "c" key
  elif key == pygame.K_c:
    # Read out the player's current coordinates
    tts.output(f"{player.x}, {player.y}, {player.z}")
  if key == pygame.K_e:
    send_data(player.turn("right"))
  if key == pygame.K_q:
    send_data(player.turn("left"))
  if key == pygame.K_z:
    tts.output(f"current location: {player.zone}")
  if key == pygame.K_h:
    tts.output(f"{player.health} health")
  if key == pygame.K_j:
    tts.output(f"{player.energy} energy")
  if key == pygame.K_f:
    tts.output(f"facing {player.direction} degrees")

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