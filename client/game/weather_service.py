# game/weather_service.py
import logging
from typing import Dict, Any
from game.weather_data import WeatherData
from speech.tts_manager import TTSManager
from audio.sound_manager import SoundManager

class WeatherService:
    """
    Phase 3 Enhancements:
    - Apply 3D audio logic for ambient weather loops.
    - If user changes device at runtime, reinit audio and reposition ambient loops.
    - Interrupt speech if needed when rapidly changing weather states.
    """

    def __init__(self, weather_data: WeatherData, tts: TTSManager, sound: SoundManager, game_state: Dict[str,Any], audio_wrapper):
        self.logger = logging.getLogger("WeatherService")
        self.weather_data = weather_data
        self.tts = tts
        self.sound = sound
        self.game_state = game_state
        self.audio_wrapper = audio_wrapper
        self.current_ambient_sound = None

    def update_weather(self, data: Dict[str, Any]):
        old_condition = self.weather_data.get_weather().get("condition", "clear")
        self.weather_data.update_weather(data)
        new_condition = data.get("condition","clear")
        intensity = data.get("intensity",0)

        # Interrupt speech if user changed weather states rapidly
        self.tts.interrupt_speech()

        if new_condition != old_condition:
            self.tts.speak(f"Weather changed to {new_condition}.")
            self.logger.debug(f"Weather changed from {old_condition} to {new_condition}.")

            # Stop old ambient sound if any
            if self.current_ambient_sound:
                self.sound.stop_sound(self.current_ambient_sound)
                self.current_ambient_sound = None

            # Depending on condition, play suitable loop
            if new_condition == "rain":
                self.current_ambient_sound = self.sound.play_ambient_loop("rain_loop.wav")
            elif new_condition == "snow":
                self.current_ambient_sound = self.sound.play_ambient_loop("windy_snow.wav")
            # For other conditions, add more loops as needed

            # Set source position for ambient loop to player position for a more 3D feel
            if self.current_ambient_sound:
                player_pos = self.game_state.get("player_position", (0,0,0))
                self.audio_wrapper.set_source_position(self.current_ambient_sound, *player_pos)
        else:
            # Same condition, intensity changed
            self.tts.speak(f"Weather intensity now {intensity}.")

    def clear_weather(self):
        self.weather_data.clear_weather()
        if self.current_ambient_sound:
            self.sound.stop_sound(self.current_ambient_sound)
            self.current_ambient_sound = None
        self.tts.interrupt_speech()
        self.tts.speak("Weather cleared, returning to normal conditions.")

    def on_audio_reinit(self):
        """
        If audio device or volume changes at runtime and openal reinit occurs,
        we could reposition or reload ambient loops.
        If ambient loop is playing, set new source position if needed.
        """
        if self.current_ambient_sound:
            player_pos = self.game_state.get("player_position", (0,0,0))
            self.audio_wrapper.set_source_position(self.current_ambient_sound, *player_pos)
        self.logger.debug("Audio reinit handled in WeatherService.")
