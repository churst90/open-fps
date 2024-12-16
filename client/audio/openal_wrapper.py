# audio/openal_wrapper.py
import logging
import os
from openal import oalInit, oalQuit, oalOpen, Listener
from openal.al import (alGenBuffers, alGenSources, alBufferData, alSourcei, alSourcef, alSourcePlay,
                       AL_FORMAT_MONO16, AL_BUFFER, AL_GAIN, AL_SOURCE_RELATIVE, AL_TRUE)
import math
import struct

class OpenALWrapper:
    def __init__(self, config):
        self.logger = logging.getLogger("OpenALWrapper")
        self.config = config
        self.volume = config.get("volume", 1.0)
        self.current_device = config.get("preferred_device", None)
        self.sounds = {}
        self.initialized = False

        self._init_device()

    def _init_device(self):
        self.logger.info("Initializing OpenAL audio device.")
        device = oalInit()
        if not device:
            self.logger.error("Failed to initialize OpenAL device, using default device.")
        else:
            self.logger.info("OpenAL device initialized successfully.")
            self.initialized = True

    def reinit_audio(self):
        self.cleanup()
        from utils.settings_manager import SettingsManager
        settings = SettingsManager.load_settings("config/settings.json") or {}
        audio_cfg = settings.get("audio", {"volume":1.0})
        self.config = audio_cfg
        self.volume = audio_cfg.get("volume", 1.0)
        self.current_device = audio_cfg.get("preferred_device", None)
        self.sounds = {}
        self._init_device()

    def load_sound(self, filepath: str):
        if not self.initialized:
            self.logger.warning("OpenAL not initialized, cannot load sound.")
            return None
        if filepath in self.sounds:
            return self.sounds[filepath]
        try:
            snd = oalOpen(filepath)
            self.sounds[filepath] = snd
            self.logger.debug(f"Loaded sound: {filepath}")
            return snd
        except Exception as e:
            self.logger.exception(f"Failed to load sound {filepath}: {e}")
            return None

    def play_sound(self, filepath: str, loop=False):
        snd = self.load_sound(filepath)
        if snd:
            snd.set_looping(loop)
            snd.play()
            self.logger.debug(f"Playing sound: {filepath}, loop={loop}")
            return snd
        return None

    def stop_sound(self, snd):
        if snd:
            snd.stop()
            self.logger.debug("Sound stopped.")

    def stop_all(self):
        for snd in self.sounds.values():
            if snd.get_state() == 4114:  # AL_PLAYING
                snd.stop()
        self.logger.debug("All sounds stopped.")

    def cleanup(self):
        if self.initialized:
            oalQuit()
            self.logger.info("OpenAL device closed.")
            self.initialized = False

    def set_listener_position(self, x, y, z):
        if not self.initialized:
            return
        Listener.set_position([x, y, z])

    def set_listener_orientation(self, at, up):
        if not self.initialized:
            return
        Listener.set_orientation(at + up)

    def set_source_position(self, snd, x, y, z):
        if snd:
            snd.set_position([x,y,z])

    def play_tone(self, frequency=440.0, duration=0.2, volume=1.0):
        if not self.initialized:
            self.logger.warning("OpenAL not initialized, cannot play tone.")
            return None, None

        sample_rate = 44100
        total_samples = int(sample_rate * duration)
        amplitude = int(32767 * volume)
        sine_wave = []

        for i in range(total_samples):
            sample_val = int(amplitude * math.sin(2 * math.pi * frequency * (i / sample_rate)))
            sine_wave.append(struct.pack('<h', sample_val))

        audio_data = b''.join(sine_wave)

        from openal.al import alGenBuffers, alGenSources, alBufferData, alSourcei, alSourcef, alSourcePlay, AL_FORMAT_MONO16, AL_BUFFER, AL_GAIN, AL_SOURCE_RELATIVE, AL_TRUE

        buffer_id = alGenBuffers(1)
        source_id = alGenSources(1)

        alBufferData(buffer_id, AL_FORMAT_MONO16, audio_data, len(audio_data), sample_rate)
        alSourcei(source_id, AL_BUFFER, buffer_id)

        # Make source relative so it doesn't depend on 3D positioning
        alSourcei(source_id, AL_SOURCE_RELATIVE, AL_TRUE)
        alSourcef(source_id, AL_GAIN, volume * self.volume)
        alSourcePlay(source_id)

        self.logger.debug(f"Playing tone freq={frequency}Hz dur={duration}s volume={volume}")
        return source_id, buffer_id
