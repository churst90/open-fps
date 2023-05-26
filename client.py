# Third party imports
import asyncio
import pickle

# Project specific imports
from menu import Menu
from gamewindow import GameWindow
from screenmanager import ScreenManager
from speechmanager import SpeechManager
from textfield import TextField

class Client:

    async def connect(self):
        try:
            self.reader, self.writer = await asyncio.open_connection(self.host, self.port)
            self.receive_task = asyncio.create_task(self.receive())
            self.tts.speak(f"Successfully connected to {self.host}:{self.port}")
        except ConnectionRefusedError:
            self.tts.speak(f"Failed to connect to {self.host}:{self.port}, connection was refused.")
        except TimeoutError:
            self.tts.speak(f"Failed to connect to {self.host}:{self.port}, connection attempt timed out.")
        except OSError as e:
            self.tts.speak(f"Failed to connect to {self.host}:{self.port} due to OS error: {str(e)}")
        except Exception as e:
            self.tts.speak(f"An unexpected error occurred: {str(e)}")

    async def disconnect(self):
        self.receive_task.cancel()
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
                for message_type, message_handler in self.message_types.items():
                    if data["type"] == message_type:
                        message_handler(data)

        except asyncio.CancelledError:
            pass

    async def create_account(self):
        self.create_account_screen = GameWindow(600, 600, "Create account dialog", self.screen_manager)
        self.screen_manager.add_screen(self.create_account_screen, "create_account_screen")
        self.screen_manager.push_screen("create_account_screen")
        self.create_account_screen.set_background((175, 175, 175))

        username_field = TextField()
        password_field = TextField()

        self.tts.speak("To create an account, please enter your username and password. Use tab and enter to navigate to and activate the buttons.")

        active_field = "username"
    
        field_names = {
            "username": "Username: edit",
            "password": "Password: edit",
            "create_account": "create_account button",
            "cancel": "Cancel button"
        }
        current_field_name = field_names[active_field]
        self.tts.speak(f"{current_field_name}")

        while True:
            events = self.create_account_screen.handle_events()
            if "KEYDOWN" in events:
                event = events["KEYDOWN"]            
                if event == self.create_account_screen.K_TAB:
                    if active_field == "username":
                        active_field = "password"
                        current_field_name = field_names[active_field]
                        self.tts.speak(f"{current_field_name} {password_field.get_text()}")
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
                        self.tts.speak(f"{current_field_name} {username_field.get_text()}")

                elif event == self.create_account_screen.K_RETURN:
                    if active_field == "create_account":
                        self.tts.speak("Trying to create a new character...")
                        try:
                            create_account_message = {"type": "create_account", "username": username_field.get_text(), "password": password_field.get_text()}
                            await self.send(create_account_message)
#                            await self.create_account_event.wait()
                            self.screen_manager.pop_screen()
                            self.screen_manager.remove_screen("create_account_screen")
#                            self.create_account_event.clear()
                            return
                        except:
                            self.tts.speak("There was an error creating a new account")
                    elif active_field == "cancel":
                        self.screen_manager.pop_screen()
                        self.screen_manager.remove_screen("create_account_screen")
                        return

                elif event == self.create_account_screen.K_BACKSPACE:
                    if active_field == "username":
                        username_field.backspace()
                    elif active_field == "password":
                        password_field.backspace()

            if "CHAR" in events:
                for char in events["CHAR"]:
                    if active_field == "username":
                        username_field.append(char)
                    elif active_field == "password":
                        password_field.append(char)

            self.screen_manager.update()
            self.create_account_screen.update()

    async def login(self):
        self.login_screen = GameWindow(600, 600, "Login dialog", self.screen_manager)
        self.screen_manager.add_screen(self.login_screen, "login_screen")
        self.screen_manager.push_screen("login_screen")
        self.login_screen.set_background((175, 175, 175))

        username_field = TextField()
        password_field = TextField()

        self.tts.speak("To login, please enter your username and password. Use tab and enter to navigate to and activate the buttons.")

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
            events = self.login_screen.handle_events()
            if "KEYDOWN" in events:
                event = events["KEYDOWN"]            
                if event == self.login_screen.K_TAB:
                    if active_field == "username":
                        active_field = "password"
                        current_field_name = field_names[active_field]
                        self.tts.speak(f"{current_field_name} {password_field.get_text()}")
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
                        self.tts.speak(f"{current_field_name} {username_field.get_text()}")

                elif event == self.login_screen.K_RETURN:
                    if active_field == "login":
                        self.tts.speak("Trying to log in...")
                        try:
                            login_message = {"type": "login", "username": username_field.get_text(), "password": password_field.get_text()}
                            await self.send(login_message)
                            await self.login_event.wait()
                            self.screen_manager.pop_screen()
                            self.screen_manager.pop_screen()
                            self.screen_manager.remove_screen("main_menu")
                            self.screen_manager.remove_screen("login_screen")
                            self.screen_manager.push_screen("main_window")
                            self.login_event.clear()
                            return
                        except:
                            self.tts.speak("There was an error logging into the server")
                    elif active_field == "cancel":
                        self.screen_manager.pop_screen()
                        self.screen_manager.remove_screen("login_screen")
                        return

                elif event == self.login_screen.K_BACKSPACE:
                    if active_field == "username":
                        username_field.backspace()
                    elif active_field == "password":
                        password_field.backspace()

            if "CHAR" in events:
                for char in events["CHAR"]:
                    if active_field == "username":
                        username_field.append(char)
                    elif active_field == "password":
                        password_field.append(char)

            self.screen_manager.update()
            self.login_screen.update()

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
        print("player load method called")
        self.tts.speak("player load method called")
        self.player = data["player_state"]
        self.map = data["map_state"]
        print(f'You are now logged in on map {data["player_state"]["current_map"]}.')
        self.tts.speak(f'You are now logged in on map {data["player_state"]["current_map"]}.')
        self.login_event.set()

    def __init__(self, host, port):
        self.login_event = asyncio.Event()
        self.screen_manager = ScreenManager()
        self.player = {"logged_in": False}
        self.map = {}
        self.tts = SpeechManager()
        self.host = host
        self.port = port
        self.private_messages = {}
        self.map_messages = {}
        self.global_messages = []
        self.error_messages = []
        self.main_window = None
        self.login_screen = None
#        self.login_screen = GameWindow(600, 600, "Login dialog", self.screen_manager)
        self.create_account_screen = None
#        self.create_account_screen = GameWindow(600, 600, "Create account dialog", self.screen_manager)
        self.message_types = {
        "login_ok": self.load_player,
#        "create_account_ok": ,
#        "create_map_ok": ,
#        "change_map_ok": ,
#        "move_ok": ,
#        "turn_ok": ,
        "chat": self.chat,
        "error": self.error_message
        }

    async def start(self):
        self.main_window = GameWindow(1200, 800, "Open FPS", self.screen_manager)
        self.main_window.set_background((0, 255, 0))
        self.screen_manager.add_screen(self.main_window, "main_window")
        self.screen_manager.push_screen("main_window")

        running = True
        while running:
            if not self.player["logged_in"]:
                self.main_menu_options = ["Create account", "Login", "About", "Exit"]
                self.main_menu  = Menu(self.main_menu_options, "Main menu", "main_menu", self.screen_manager)
                selected_option = await self.main_menu.create()
                if selected_option == "Create account":
                    await self.create_account()
                elif selected_option == "Login":
                    await self.login()
                elif selected_option == "About":
                    self.tts.speak("Open fps by Cody Hurst. This game is a beta and should be treated as such.")
                elif selected_option == "Exit":
                    self.screen_manager.pop_screen()
                    running = False
                    await asyncio.sleep(0)
            else:
                self.tts.speak("ready for game input")

            self.screen_manager.update()
            self.main_window.update()

async def main():
    loop = asyncio.get_event_loop()
    client = Client("localhost", 33288)
    try:
        await client.connect()
        await client.start()
#        await asyncio.gather(client.start(), client.connect())
    except KeyboardInterrupt:
        pass

if __name__ == '__main__':
    asyncio.run(main())