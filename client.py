# Third party imports
import asyncio
import pickle

# Project specific imports
from menu import Menu
from gamewindow import GameWindow
from player import Player
from screenmanager import ScreenManager
from speechmanager import SpeechManager

class Client:

    async def connect(self):
        self.reader, self.writer = await asyncio.open_connection(self.host, self.port)

    async def disconnect(self):
        await self.writer.close()
        await self.writer.wait_closed()

    async def send(self, data):
        serialized_data = pickle.dumps(data)
        self.writer.write(serialized_data)
        await self.writer.drain()

    async def receive(self):
        try:
            while True:
                data = await self.reader.read(4096)
                if not data:
                    break
                data = pickle.loads(data)
                for message_type, message_handler in self.messageTypes.items():
                    if data["type"] == message_type:
                        self.messageTypes[message_type](data)

        except asyncio.CancelledError:
            pass

    async def create_account(self):
        create_account_screen = GameWindow(500, 500, "Create account dialog", self.screen_manager)
        self.screen_manager.add_screen(create_account_screen)
        self.screen_manager.set_active_screen(create_account_screen)

        self.tts.speak("To create a new character, select a username and password and press enter on 'create account'")

        username = ""
        password = ""
        active_field = "username"
        field_names = {
            "username": "Username: edit",
            "password": "Password: edit",
            "create_account": "Create Account button",
            "cancel": "Cancel button"
        }
        current_field_name = field_names[active_field]
        self.tts.speak(f"{current_field_name}")

        while True:
            self.screen_manager.update()
            events = create_account_screen.handle_events()
            for event in events:
                if event.type == create_account_screen.KEYDOWN:
                    if event.key == create_account_screen.K_TAB:
                        if active_field == "username":
                            active_field = "password"
                            current_field_name = field_names[active_field]
                            self.tts.speak(f"{current_field_name} {password}")
                        elif active_field == "password":
                            active_field = "create_account"
                            current_field_name = field_names[active_field]
                            self.tts.speak(f"{current_field_name}")
                        elif active_field == "create_account":
                            active_field = "cancel"
                            current_field_name = field_names[active_field]
                            self.tts.speak(f"{current_field_name}")
                        elif active_field == "cancel":
                            active_field = "username"
                            current_field_name = field_names[active_field]
                            self.tts.speak(f"{current_field_name}, {username}")

                    elif event.key == create_account_screen.K_BACKSPACE:
                        # Handle backspace key press
                        if active_field == "username":
                            username = username[:-1]
                        elif active_field == "password":
                            password = password[:-1]
                    elif event.key == create_account_screen.K_RETURN:
                        # Handle return key press
                        if active_field == "create_account":
                            self.tts.speak("Creating account...")
                            # Add your create account logic here

                        elif active_field == "cancel":
                            create_account_screen.close()

                    if active_field == "username":
                        username = create_account_screen.get_text_input(events)
                    elif active_field == "password":
                        password = create_account_screen.get_text_input(events)

    async def login(self):
        login_screen = GameWindow(500, 500, "Login dialog", self.screen_manager)
        self.screen_manager.add_screen(login_screen)
        self.screen_manager.set_active_screen(login_screen)

        self.tts.speak("To login, please enter your username and password. Use tab and enter to navigate to and activate the buttons.")

        username = ""
        password = ""
        active_field = "username"
        field_names = {
            "username": "Username: edit",
            "password": "Password: edit",
            "login": "Login button",
            "cancel": "Cancel button"
        }
        current_field_name = field_names[active_field]
        self.tts.speak(f"{current_field_name}")

        while True:
            self.screen_manager.update()
            events = login_screen.handle_events()
            for event in events:
                if event.type == login_screen.KEYDOWN:
                    if event.key == login_screen.K_TAB:
                        if active_field == "username":
                            active_field = "password"
                            current_field_name = field_names[active_field]
                            self.tts.speak(f"{current_field_name} {password}")
                        elif active_field == "password":
                            active_field = "login"
                            current_field_name = field_names[active_field]
                            self.tts.speak(f"{current_field_name}")
                        elif active_field == "login":
                            active_field = "cancel"
                            current_field_name = field_names[active_field]
                            self.tts.speak(f"{current_field_name}")
                        elif active_field == "cancel":
                            active_field = "username"
                            current_field_name = field_names[active_field]
                            self.tts.speak(f"{current_field_name}, {username}")

                    elif event.key == login_screen.K_BACKSPACE:
                        # Handle backspace key press
                        if active_field == "username":
                            username = username[:-1]
                        elif active_field == "password":
                            password = password[:-1]
                    elif event.key == login_screen.K_RETURN:
                        # Handle return key press
                        if active_field == "login":
                            self.tts.speak("Trying to connect to the server...")
                            await self.connect()
                            self.tts.speak("Trying to log in...")
                            login_message = {"type": "login", "username": username, "password": password}
                            await self.send(login_message)
                            # Add your create account logic here

                        elif active_field == "cancel":
                            return

                    if active_field == "username":
                        username = login_screen.get_text_input(events)
                    elif active_field == "password":
                        password = login_screen.get_text_input(events)

    def __init__(self, host, port):
        self.screen_manager = ScreenManager()
        self.tts = SpeechManager()
        self.host = host
        self.port = port

    async def start(self):
        main_window = GameWindow(600, 600, "Open FPS", self.screen_manager)
        self.screen_manager.add_screen(main_window)
        self.screen_manager.set_active_screen(main_window)
        main_window.set_background((0, 50, 200))

        running = True
        while running:
#            self.screen_manager.update()

            start_menu_options = ["Create account", "Login", "About", "Exit"]
            start_menu  = Menu(start_menu_options, "Start menu", self.screen_manager)
            selected_option = start_menu.create()
            if selected_option == "Create account":
                await self.create_account()
            elif selected_option == "Login":
                await self.login()
            elif selected_option == "About":
                self.tts.speak("Open fps by Cody Hurst. This game is a beta and should be treated as such.")
            elif selected_option == "Exit":
                main_window.close()
                running = False

async def main():
    loop = asyncio.get_event_loop()
    client = Client("localhost", 33288)
    try:
        await client.start()
    except KeyboardInterrupt:
        pass

if __name__ == '__main__':
    asyncio.run(main())