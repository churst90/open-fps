import asyncio
import json

# Assuming Dialog, GameWindow, ScreenManager, Menu, TextField are previously defined in other modules
from dialog import Dialog
from gamewindow import GameWindow
from menu import Menu
from textfield import TextField

class ClientHandler:
    def __init__(self, screen_manager, network, tts):
        self.tts = tts
        self.screen_manager = screen_manager
        self.network = network
        self.player = {"logged_in": False, "position": None, "orientation": None}
        self.game_window = None  # Placeholder for the GameWindow instance

    async def create_account(self, host, port):
        # Set the network host and port for the connection
        self.network.set_host(host)
        self.network.set_port(port)

        # Initialize and display the dialog for account creation
        account_creation_dialog = Dialog(
            title="Create Account",
            screen_manager=self.screen_manager,
            field_names=["username", "password"],
            network=self.network,
            tts=self.tts
        )

        # Handle the dialog interaction and get input data
        account_creation_input_json = await account_creation_dialog.handle_dialog()

        # Process the account creation input
        account_creation_data = json.loads(account_creation_input_json)
        if account_creation_data["type"] == "Create Account":
            username = account_creation_data["fields"]["username"]
            password = account_creation_data["fields"]["password"]
            # Implement the logic to attempt account creation with the provided username and password
            try:
                create_account_message = {"type": "create_account", "username": username, "password": password}
                await self.network.connect()
                await self.network.send(create_account_message)
                # Handle the response from the server regarding account creation success or failure
                # This might involve waiting for a response message from the server and processing it
                self.tts.speak("Account created successfully. Please log in.")
            except Exception as e:
                self.tts.speak(f"There was an error creating the account: {e}")
                # Optionally, implement retry logic or return to the previous menu

        # Ensure cleanup after dialog use, which might be handled within the Dialog class itself

    async def login(self, host, port):
        # Set the network host and port for the connection
        self.network.set_host(host)
        self.network.set_port(port)

        # Create the login dialog
        login_dialog = Dialog(
            title="Login",
            screen_manager=self.screen_manager,
            field_names=["username", "password"],
            network=self.network,
            tts=self.tts
        )

        # Handle the dialog interaction and collect input
        login_input_data = await login_dialog.handle_dialog()

        # Proceed with the login process using the collected data
        if login_input_data["type"] == "Login":
            try:
                username = login_input_data["fields"]["username"]
                password = login_input_data["fields"]["password"]

                # Prepare the login message
                login_message = {
                    "type": "login",
                    "username": username,
                    "password": password
                }

                # Send the login message to the server
                await self.network.connect()
                await self.network.send(login_message)

                # Handle server response here
                # You might want to receive a response from the server indicating success or failure
                # Example: response = await self.network.receive()
                # Check the response and proceed accordingly

            except Exception as e:
                # Handle any errors that occur during the login process
                self.tts.speak(f"There was an error logging into the server: {e}")

    async def attempt_login(self, username, password):
        # Logic to handle the actual login attempt
        login_message = {"type": "login", "username": username, "password": password}
        await self.network.connect()
        await self.network.send(login_message)
        # Further processing based on server response...
        # For example, wait for a login confirmation message

    def error_message(self, data):
        self.error_messages.append(data["message"])
        self.tts.speak(data["message"])

    def chat(self, data):
        if data["scope"] == "private":
            self.private_messages.append(data["message"])
            self.tts.speak(data["message"])
        if data["scope"] == "map":
            self.map_messages.append(data["message"])
            self.tts.speak(data["message"])
        if data["scope"] == "global":
            self.global_messages.append(data["message"])
            print(data["message"])
            self.tts.speak(data["message"])

    def load_player(self, data):
        self.player['logged_in'] = True
        self.player['position'] = data['player_state']['position']
        self.player['orientation'] = data['player_state']['orientation']
        self.map = data['map_state']
        self.tts.speak(f"You are now logged in. Current location: {self.player['position']}.")

        # Initialize the game window for receiving keyboard inputs
        self.initialize_game_window()

    async def main_menu(self):
        # Implementation of main menu
        main_menu_options = ["Create account", "Login", "Exit"]
        main_menu = Menu(main_menu_options, "Main Menu", "main_menu", self.screen_manager)
        selected_option = await main_menu.create()
        return selected_option

    async def send_movement_command(self, command):
        await self.network.send({"type": "move", "command": command})

    def initialize_game_window(self):
        # Assuming the GameWindow has already been created and added to the ScreenManager in the Client
        self.game_window = self.screen_manager.get_current_screen()

        # Start listening for input if not already doing so
        if not self.game_window.listening_for_input:
            asyncio.create_task(self.listen_for_input())
            self.game_window.listening_for_input = True

    async def listen_for_input(self):
        # This replaces direct pygame event handling
        running = True
        while running:
            events = self.game_window.handle_events()
            if "QUIT" in events:
                running = False
            elif "KEYDOWN" in events:
                await self.process_keydown(events["KEYDOWN"])

    async def process_keydown(self, key):
        # Mapping pygame keys to movement commands
        command_map = {
            GameWindow.K_LEFT: "left",
            GameWindow.K_RIGHT: "right",
            GameWindow.K_UP: "forward",
            GameWindow.K_DOWN: "backward",
            GameWindow.K_q: "turn_left",
            GameWindow.K_e: "turn_right",
            GameWindow.K_v: "look_up",
            GameWindow.K_f: "look_down"
        }
        if key in command_map:
            await self.send_movement_command(command_map[key])

    async def handle_server_messages(self):
        while self.player['logged_in']:
            message = await self.network.receive()
            if message:
                if message['type'] == 'update_position':
                    self.update_player_position(message)
                # Add handling for other message types as needed

    def update_player_position(self, message):
        self.player['position'] = message['new_position']
        if 'new_orientation' in message:
            self.player['orientation'] = message['new_orientation']
        self.tts.speak("Position updated.")