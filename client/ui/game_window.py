# ui/game_window.py
import tkinter as tk
import logging
from typing import Dict, Any
from utils.keybinding_manager import KeybindingManager

class GameWindow(tk.Frame):
    """
    The main in-game UI after the user logs in.
    Displays some basic status text and captures keyboard input to control the character.
    Integrates with the keybinding manager to map keys to actions.
    In future, it will connect to the server to update player position, map data, etc.
    """

    def __init__(self, master, tts_manager, sound_manager, settings: Dict[str, Any], keybinding_manager: KeybindingManager):
        super().__init__(master)
        self.logger = logging.getLogger("GameWindow")
        self.tts = tts_manager
        self.sound = sound_manager
        self.settings = settings
        self.keybinds = keybinding_manager

        self.master = master
        self.master.title("Game Window")
        self.pack(fill="both", expand=True)

        # Status label to show player info, map info, etc.
        self.status_label = tk.Label(self, text="You are in the game world. Press keys to move.", font=("Arial", 14))
        self.status_label.pack(pady=20)

        # Bind keys
        self.master.bind("<Key>", self.on_key_press)

        self.tts.speak("Game window opened. Use your configured keys to move or interact.")

    def on_key_press(self, event):
        key = event.keysym
        action = self.keybinds.get_action_for_key(key)
        if action:
            self.handle_action(action)
        else:
            self.tts.speak(f"No action bound to {key}")

    def handle_action(self, action: str):
        """
        Handle actions triggered by key presses.
        For now, we just TTS the action. In the future:
        - Send move requests to server
        - Update player position
        - Interact with items, etc.
        """
        # Simple feedback for now
        self.tts.speak(f"Action: {action}")
        if action.startswith("move_"):
            self.sound.play_menu_sound("footsteps.wav")
            # In future: send user_move_request to server, update status
        else:
            # For other actions like jump, interact, etc.
            pass
