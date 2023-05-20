from gamewindow import GameWindow
#from screenmanager import ScreenManager
from speechmanager import SpeechManager

class ClientHandler:
    def __init__(self, screen_manager):
        self.tts = SpeechManager()
        self.screen_manager = screen_manager

    def create_account(self):
        create_account_screen = GameWindow(500, 500, "Create account", self.screen_manager)
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
                            return

                    if active_field == "username":
                        username = create_account_screen.get_text_input()
                    elif active_field == "password":
                        password = create_account_screen.get_text_input()
