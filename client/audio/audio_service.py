# audio/audio_service.py
import logging
from typing import Dict, Any
import os
from .sound_manager import SoundManager
from .openal_wrapper import OpenALWrapper

class AudioService:
    def __init__(self, config: Dict[str, Any]):
        self.logger = logging.getLogger("AudioService")

        audio_cfg = config.get("audio", {})
        self.categories = {
            "music": audio_cfg.get("music_volume", 1.0),
            "environment": audio_cfg.get("environment_volume", 1.0),
            "tiles": audio_cfg.get("tiles_volume", 1.0),
            "effects": audio_cfg.get("effects_volume", 1.0)
        }

        self.doppler_factor = audio_cfg.get("doppler_factor", 1.0)
        self.rolloff_factor = audio_cfg.get("rolloff_factor", 1.0)
        self.ref_distance = audio_cfg.get("ref_distance", 1.0)
        self.max_distance = audio_cfg.get("max_distance", 50.0)

        self.use_efx = audio_cfg.get("use_efx", False)
        self.reverb_preset = audio_cfg.get("reverb_preset", "generic")

        self.sound_manager = SoundManager(audio_cfg)
        self.wrapper = self.sound_manager.audio

        self.sound_cache = {}
        self.logger.info("AudioService initialized with categories and advanced features.")

        self._set_doppler(self.doppler_factor)
        self._set_distance_model()

        if self.use_efx:
            self._init_efx()
            self._apply_reverb_preset(self.reverb_preset)

    def _set_doppler(self, factor: float):
        from openal.al import alDopplerFactor
        alDopplerFactor(factor)
        self.logger.debug(f"Doppler factor set to {factor}")

    def _set_distance_model(self):
        from openal.al import alDistanceModel, AL_INVERSE_DISTANCE_CLAMPED
        alDistanceModel(AL_INVERSE_DISTANCE_CLAMPED)
        self.logger.debug("Distance model set to inverse distance clamped.")

    def _init_efx(self):
        self.logger.info("Initializing EFX extensions for reverb/occlusion (if supported).")
        # EFX initialization code with ctypes if needed

    def _apply_reverb_preset(self, preset: str):
        self.logger.debug(f"Applying reverb preset: {preset}")
        # Set EAXReverb properties for the chosen preset via EFX calls if needed.

    def load_sound(self, filepath: str, category: str = "effects"):
        if filepath in self.sound_cache:
            self.sound_cache[filepath]["refcount"] += 1
        else:
            snd = self.sound_manager.audio.load_sound(filepath)
            if snd:
                self.sound_cache[filepath] = {"refcount": 1, "category": category}
            else:
                self.logger.warning(f"Failed to load sound {filepath} in category {category}.")
                return None
        return filepath

    def unload_sound(self, filepath: str):
        if filepath in self.sound_cache:
            self.sound_cache[filepath]["refcount"] -= 1
            if self.sound_cache[filepath]["refcount"] <= 0:
                # Remove from OpenAL
                snd_obj = self.sound_manager.audio.sounds.pop(filepath, None)
                del self.sound_cache[filepath]
                self.logger.debug(f"Unloaded sound {filepath}.")
        else:
            self.logger.warning(f"Tried to unload sound {filepath} not in cache.")

    def play_sound(self, filepath: str, x: float=0, y: float=0, z: float=0, loop=False):
        if filepath not in self.sound_cache:
            self.logger.warning(f"Sound {filepath} not loaded before play_sound.")
            return None

        category = self.sound_cache[filepath]["category"]
        base_snd = self.sound_manager.audio.play_sound(filepath, loop=loop)
        if base_snd:
            self.wrapper.set_source_position(base_snd, x, y, z)
            cat_vol = self.categories.get(category,1.0)
            base_snd.set_gain(cat_vol * self.wrapper.volume)
        return base_snd

    def set_listener_position(self, x,y,z):
        self.wrapper.set_listener_position(x,y,z)

    def set_listener_orientation(self, at, up):
        self.wrapper.set_listener_orientation(at, up)

    def set_category_volume(self, category: str, volume: float):
        if category in self.categories:
            self.categories[category] = volume
            self.logger.debug(f"Category {category} volume set to {volume}.")
            # If we had a list of currently playing sounds per category, update their gain here.
        else:
            self.logger.warning(f"Unknown category {category}.")

    def reinit_audio(self):
        self.logger.info("Reinitializing audio due to device/volume changes.")
        self.sound_manager.audio.reinit_audio()
        # Reload sounds
        for filepath, info in self.sound_cache.items():
            snd = self.sound_manager.audio.load_sound(filepath)
            if snd:
                self.sound_manager.audio.sounds[filepath] = snd

    def cleanup(self):
        self.sound_manager.cleanup()
        self.logger.info("AudioService cleaned up.")

    def _apply_occlusion_filter(self, snd, wall_factor: float):
        # Hypothetical EFX call for occlusion (low-pass filter)
        pass
