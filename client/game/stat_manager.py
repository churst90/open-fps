# game/stat_manager.py
import logging
from typing import Dict, Any

class StatManager:
    """
    Manages player stats such as experience points (XP), level, or rank.
    The server might send xp_gain events or level_up events.
    This module tracks these stats and provides methods for updating them.
    """

    def __init__(self):
        self.logger = logging.getLogger("StatManager")
        self.xp: int = 0
        self.level: int = 1
        self.rank: str = "Newbie"  # Example rank
        # Additional stats like kills, deaths, quests_completed can be tracked here

    def load_stats(self, data: Dict[str, Any]):
        """
        Load stats from a dictionary, e.g. after login.
        data might contain {"xp": ..., "level": ..., "rank": ...}
        """
        self.xp = data.get("xp", 0)
        self.level = data.get("level", 1)
        self.rank = data.get("rank", "Newbie")
        self.logger.debug(f"Loaded stats: XP={self.xp}, Level={self.level}, Rank={self.rank}")

    def add_xp(self, amount: int):
        """
        Add XP and check if level up is needed.
        """
        old_level = self.level
        self.xp += amount
        self.logger.debug(f"Added {amount} XP. Total XP now {self.xp}.")
        self._check_level_up()

        if self.level > old_level:
            self.logger.info(f"Level up! Now level {self.level}.")

    def _check_level_up(self):
        """
        Check if current XP surpasses threshold for next level.
        For simplicity, say each level requires level*100 XP:
        level 1->2 at 100 XP, 2->3 at 200 XP, etc.
        """
        required = self.level * 100
        while self.xp >= required:
            self.level += 1
            self.logger.debug(f"Level increased to {self.level}.")
            required = self.level * 100

    def set_rank(self, new_rank: str):
        self.rank = new_rank
        self.logger.debug(f"Rank updated to {new_rank}.")

    def get_stats(self) -> Dict[str, Any]:
        return {"xp": self.xp, "level": self.level, "rank": self.rank}
