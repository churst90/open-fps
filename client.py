# Project specific imports
from menu import Menu
from gamewindow import GameWindow
from player import Player
from clienthandler import ClientHandler
from screenmanager import ScreenManager
from speechmanager import SpeechManager

class Client:
    def __init__(self):
        self.screen_manager = ScreenManager()
        self.client_handler = ClientHandler(self.screen_manager)
        self.tts = SpeechManager()

    def start(self):
        main_window = GameWindow(600, 600, "Open FPS", self.screen_manager)
        clock = main_window.get_clock()

        running = True
        while running:
            main_window.set_background((0, 50, 200))

            start_menu_options = ["Create account", "Login", "About", "Exit"]
            start_menu = Menu(start_menu_options, "Start menu", self.screen_manager)
            selected_option = start_menu.create()
            if selected_option == "Create account":
                self.client_handler.create_account()
            elif selected_option == "Login":
                self.client_handler.login()
            elif selected_option == "About":
                self.tts.speak("Open fps by Cody Hurst. This game is a beta and should be treated as such.")
            elif selected_option == "Exit":
                running = False

            self.screen_manager.update()
            clock.tick(60)

def main():
    server = Client()
    server.start()

if __name__ == "__main__":
        main()
