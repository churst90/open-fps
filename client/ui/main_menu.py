# ui/main_menu.py
import tkinter as tk
import logging

from ui.login_dialog import LoginDialog
from ui.create_account_dialog import CreateAccountDialog
# If you have a server_list_dialog.py, import it:
# from ui.server_list_dialog import ServerListDialog

class MainMenu(tk.Frame):
    """
    Main menu with arrow keys navigation.
    Options:
    - Play Game
    - Login
    - Create Account
    - Server List
    - Quit

    Uses tts.speak() for announcements and audio_service.sound_manager.audio.play_tone() for tones.
    """

    def __init__(self, master, tts_manager, audio_service, settings):
        super().__init__(master)
        self.logger = logging.getLogger("MainMenu")

        self.tts = tts_manager
        self.audio_service = audio_service
        self.settings = settings

        self.options = ["Play Game", "Login", "Create Account", "Server List", "Quit"]
        self.current_index = 0

        self.label = tk.Label(self, text=self.options[self.current_index], font=("Arial", 16))
        self.label.pack(pady=20)

        self.bind_all("<Up>", self.on_up)
        self.bind_all("<Down>", self.on_down)
        self.bind_all("<Return>", self.on_enter)

        self.tts.interrupt_speech()
        self.tts.speak("Main menu loaded. Use up and down arrow keys to navigate. Press Enter to select.")
        self.audio_service.sound_manager.audio.play_tone(frequency=1000, duration=0.05, volume=0.5)

        self._announce_current_option()

    def _announce_current_option(self):
        self.tts.interrupt_speech()
        current_option = self.options[self.current_index]
        self.tts.speak(f"{current_option}")
        self.audio_service.sound_manager.audio.play_tone(frequency=880, duration=0.05, volume=0.5)
        self.label.config(text=current_option)

    def on_up(self, event=None):
        # wrap-around logic
        if self.current_index == 0:
            self.current_index = len(self.options)-1
        else:
            self.current_index -= 1
        self._announce_current_option()

    def on_down(self, event=None):
        # wrap-around logic
        if self.current_index == len(self.options)-1:
            self.current_index = 0
        else:
            self.current_index += 1
        self._announce_current_option()

    def on_enter(self, event=None):
        selection = self.options[self.current_index]
        self.tts.interrupt_speech()
        self.tts.speak(f"You selected {selection}.")
        self.audio_service.sound_manager.audio.play_tone(frequency=660, duration=0.1, volume=0.5)

        if selection == "Quit":
            self.master.destroy()
        elif selection == "Login":
            # Open login dialog
            LoginDialog(self.master, self.tts, self.audio_service)
        elif selection == "Create Account":
            # Open create account dialog
            CreateAccountDialog(self.master, self.tts, self.audio_service)
        elif selection == "Server List":
            # If implemented:
            # ServerListDialog(self.master, self.tts, self.audio_service)
            self.tts.interrupt_speech()
            self.tts.speak("Server list dialog would open here.")
        elif selection == "Play Game":
            self.tts.interrupt_speech()
            self.tts.speak("Starting the game. Not implemented.")
