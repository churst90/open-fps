# utils/keybinding_manager.py
import logging
from typing import Dict, Any, Optional
from utils.settings_manager import SettingsManager
import os

class KeybindingManager:
    """
    Manages keybindings for the game.
    keybindings.json expected format:
    {
        "actions": {
            "move_forward": "Up",
            "move_backward": "Down",
            "turn_left": "Left",
            "turn_right": "Right",
            "jump": "space"
        }
    }

    Actions map to keys. This class can be extended to allow runtime changes and saving back.
    """

    def __init__(self, filepath: str = "config/keybindings.json"):
        self.logger = logging.getLogger("KeybindingManager")
        self.filepath = filepath
        self.bindings = self._load_keybindings()

    def _load_keybindings(self) -> Dict[str, str]:
        data = SettingsManager.load_settings(self.filepath)
        if data and "actions" in data:
            self.logger.info(f"Loaded keybindings from {self.filepath}.")
            return data["actions"]
        else:
            self.logger.warning(f"No keybindings found in {self.filepath}, using defaults.")
            # Provide some defaults
            return {
                "move_forward": "Up",
                "move_backward": "Down",
                "turn_left": "Left",
                "turn_right": "Right",
                "jump": "space"
            }

    def get_action_for_key(self, key: str) -> Optional[str]:
        """
        Given a key pressed, return the corresponding action if any.
        Key should match the exact keysym used by Tkinter (e.g. "Up", "Down", etc.)
        """
        for action, bound_key in self.bindings.items():
            if bound_key == key:
                return action
        return None

    def set_binding(self, action: str, key: str) -> bool:
        """
        Set or update the keybinding for a given action.
        Returns True if updated, False if action not known.
        """
        if action in self.bindings:
            self.bindings[action] = key
            return True
        else:
            self.logger.warning(f"Action '{action}' not known.")
            return False

    def save_keybindings(self) -> bool:
        data = {"actions": self.bindings}
        if not os.path.exists("config"):
            os.makedirs("config")
        return SettingsManager.save_settings(self.filepath, data)
