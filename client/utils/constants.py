# utils/constants.py
"""
A central place to store global constants used throughout the client.
This makes it easy to manage and change values in one location.
"""

# Server Defaults
DEFAULT_HOST = "localhost"
DEFAULT_PORT = 33288
USE_SSL = True

# Audio Defaults
DEFAULT_VOLUME = 1.0  # float from 0.0 to 1.0
DEFAULT_RATE = 200    # words per minute for TTS

# Keybindings Defaults
DEFAULT_KEYBINDINGS = {
    "move_forward": "Up",
    "move_backward": "Down",
    "turn_left": "Left",
    "turn_right": "Right",
    "jump": "space"
}

# Other general constants
GAME_TITLE = "Open Audio Game Client"
LOG_DIR = "logs"
CONFIG_DIR = "config"
