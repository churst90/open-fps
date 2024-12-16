# game/player_data.py
import logging
from typing import Tuple

class PlayerData:
    """
    Stores player-specific information like username, position, health, energy, etc.
    The message handler updates this upon receiving move results or health updates from server.
    """

    def __init__(self):
        self.logger = logging.getLogger("PlayerData")
        self.username: str = ""
        self.token: str = ""  # For authenticated requests
        self.current_map: str = "Main"
        self.current_zone: str = "Main"
        self.position: Tuple[float,float,float] = (0.0,0.0,0.0)
        self.yaw: float = 0.0
        self.pitch: float = 0.0
        self.health: int = 10000
        self.energy: int = 10000
        self.inventory = {}
        self.logged_in = False
        self.role = "player"

    def set_credentials(self, username: str, token: str):
        self.username = username
        self.token = token
        self.logged_in = True
        self.logger.debug(f"Credentials set for {username}, token acquired.")

    def update_position(self, new_pos: Tuple[float,float,float]):
        self.position = new_pos
        self.logger.debug(f"Position updated to {new_pos}.")

    def update_health(self, new_health: int):
        self.health = max(0, new_health)
        self.logger.debug(f"Health updated to {self.health}.")

    def update_energy(self, new_energy: int):
        self.energy = max(0, new_energy)
        self.logger.debug(f"Energy updated to {self.energy}.")

    def logout(self):
        self.username = ""
        self.token = ""
        self.logged_in = False
        self.logger.debug("Player logged out.")
