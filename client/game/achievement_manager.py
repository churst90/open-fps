# game/achievement_manager.py
import logging
from typing import Dict, Any
from speech.tts_manager import TTSManager
from audio.sound_manager import SoundManager

class AchievementManager:
    """
    Tracks achievements unlocked by the player.
    The server might send events like 'achievement_unlocked' with an achievement_id.
    This manager stores them and can provide a list of unlocked achievements.
    """

    def __init__(self, tts: TTSManager, sound: SoundManager):
        self.logger = logging.getLogger("AchievementManager")
        self.tts = tts
        self.sound = sound
        self.unlocked: Dict[str, Dict[str, Any]] = {}
        # unlocked[achievement_id] = {"description":..., "xp_reward":..., ...}

    def unlock_achievement(self, achievement_id: str, data: Dict[str, Any]):
        """
        Record that the player unlocked a new achievement.
        data might contain {"description": "Completed first quest.", "xp_reward": 100, "unlocks": ...}
        """
        if achievement_id in self.unlocked:
            self.logger.debug(f"Achievement {achievement_id} already unlocked.")
            return

        self.unlocked[achievement_id] = data
        desc = data.get("description", "An achievement")
        self.logger.info(f"Achievement unlocked: {desc}")
        self.tts.speak(f"Achievement unlocked: {desc}")
        self.sound.play_menu_sound("menu_click.wav")

    def list_achievements(self) -> Dict[str, Dict[str,Any]]:
        """
        Return all unlocked achievements.
        """
        return self.unlocked
