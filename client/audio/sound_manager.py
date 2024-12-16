# audio/sound_manager.py
import logging
import os
from typing import Dict, Any
from .openal_wrapper import OpenALWrapper

class SoundManager:
    """
    High-level sound manager that uses OpenALWrapper.
    Loads menu sounds, ambient loops, and can play/stop them easily.
    """

    def __init__(self, config: Dict[str, Any]):
        """
        config can contain audio settings like default volume, etc.
        For simplicity, this example does not adjust volume globally.
        """
        self.logger = logging.getLogger("SoundManager")
        self.audio = OpenALWrapper(config)
        self.sounds_dir = "audio/sounds"
        self.logger.info("SoundManager initialized.")

    def play_menu_sound(self, sound_name: str):
        """
        Play a UI/menu-related sound once.
        """
        filepath = os.path.join(self.sounds_dir, sound_name)
        self.audio.play_sound(filepath, loop=False)

    def play_ambient_loop(self, sound_name: str):
        """
        Play an ambient looping sound, e.g. background noise or music.
        Returns the Sound object so caller can stop it.
        """
        filepath = os.path.join(self.sounds_dir, sound_name)
        return self.audio.play_sound(filepath, loop=True)

    def stop_sound(self, snd):
        """
        Stop a playing sound object.
        """
        self.audio.stop_sound(snd)

    def stop_all_sounds(self):
        """
        Stop all currently playing sounds.
        """
        self.audio.stop_all()

    def cleanup(self):
        """
        Cleanup audio resources.
        """
        self.audio.cleanup()
        self.logger.info("SoundManager cleaned up.")
