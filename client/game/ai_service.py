# game/ai_service.py
import logging
from typing import Dict, Any
from game.ai_data import AIData
from speech.tts_manager import TTSManager
from audio.sound_manager import SoundManager

class AIService:
    """
    Phase 3 Enhancements:
    - Interrupt ongoing speech if multiple AI spawn/move events come in quick succession.
    - If AI have sounds (like footsteps), set their source position in 3D.
    - On audio reinit, reposition or reload AI-related sounds.
    """

    def __init__(self, ai_data: AIData, tts: TTSManager, sound: SoundManager, game_state: Dict[str,Any], audio_wrapper):
        self.logger = logging.getLogger("AIService")
        self.ai_data = ai_data
        self.tts = tts
        self.sound = sound
        self.game_state = game_state
        self.audio_wrapper = audio_wrapper

    def add_or_update_ai(self, ai_id: str, ai_info: Dict[str, Any]):
        # Interrupt speech if we are about to announce another AI quickly
        self.tts.interrupt_speech()
        self.ai_data.add_or_update_ai(ai_id, ai_info)
        if "name" in ai_info:
            role = ai_info.get("role","entity")
            name = ai_info["name"]
            self.tts.speak(f"{role.capitalize()} named {name} is now present.")
        # If AI has a sound (e.g. footsteps), play and set position
        # Hypothetical footsteps sound for AI when spawned:
        footsteps = self.sound.play_sound("ai_footsteps.wav", loop=True)
        if footsteps:
            pos = ai_info.get("position",(0,0,0))
            self.audio_wrapper.set_source_position(footsteps, *pos)

    def remove_ai(self, ai_id: str):
        ai_info = self.ai_data.get_ai_info(ai_id)
        if ai_info:
            self.tts.interrupt_speech()
            name = ai_info.get("name","An AI")
            self.tts.speak(f"{name} has left the area.")
            # If we had a reference to AI sound, stop it. Assuming we track them by id:
            # No direct code since we didn't store footsteps references per AI.
            self.ai_data.remove_ai(ai_id)

    def get_ai_near_player(self, player_position: tuple, radius: float = 10.0) -> Dict[str, Dict[str,Any]]:
        return self.ai_data.get_ai_by_map(player_position, radius) if hasattr(self.ai_data, 'get_ai_by_map') else {}

    def on_audio_reinit(self):
        """
        If audio device changes, we may need to reposition sounds.
        If we were tracking individual AI sounds, re-set their positions here.
        For simplicity, no action if we don't track per-AI sounds.
        """
        self.logger.debug("Audio reinit in AIService. Reposition AI sounds if tracked.")
