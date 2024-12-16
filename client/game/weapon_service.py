# game/weapon_service.py
import logging
from typing import Dict, Any
from game.weapon_data import WeaponData
from speech.tts_manager import TTSManager
from audio.sound_manager import SoundManager
from game.inventory_manager import InventoryManager

class WeaponService:
    """
    Phase 3 Enhancements:
    - Apply 3D audio logic: set source position of weapon firing sounds to player's position.
    - If the audio device or volume changes at runtime (reinit_audio()), the sound_manager handles it, 
      but we can also re-select weapon sounds if needed.
    - If user quickly switches weapons or fires multiple times, consider calling tts.interrupt_speech() 
      before announcing new weapon to keep speech responsive.
    """

    def __init__(self, weapon_data: WeaponData, inventory: InventoryManager, tts: TTSManager, sound: SoundManager, game_state: Dict[str,Any], audio_wrapper):
        self.logger = logging.getLogger("WeaponService")
        self.weapon_data = weapon_data
        self.inventory = inventory
        self.tts = tts
        self.sound = sound
        self.game_state = game_state
        self.audio_wrapper = audio_wrapper  # to call reinit if needed
        self.equipped_weapon_id: str = ""

    def equip_weapon(self, weapon_id: str):
        info = self.weapon_data.get_weapon_info(weapon_id)
        if not info:
            self.tts.speak("That weapon is not known.")
            return False
        if self.inventory.get_item_quantity(weapon_id) > 0:
            # Interrupt speech if user previously triggered speech that hasn't finished
            self.tts.interrupt_speech()
            self.equipped_weapon_id = weapon_id
            name = info.get("name","Unknown weapon")
            self.tts.speak(f"Equipped {name}.")
            self.logger.debug(f"Equipped weapon {weapon_id}.")
            return True
        else:
            self.tts.speak("You don't have that weapon in your inventory.")
            return False

    def fire_weapon(self):
        if not self.equipped_weapon_id:
            self.tts.speak("No weapon equipped.")
            return
        ammo = self.inventory.get_item_quantity(self.equipped_weapon_id)
        if ammo <= 0:
            self.tts.speak("No ammo left. Need to reload.")
            return
        self.inventory.remove_item(self.equipped_weapon_id, 1)
        self.tts.speak("Firing weapon.")
        
        # Play firing sound in 3D at player's position
        player_pos = self.game_state.get("player_position", (0,0,0))
        firing_sound = self.sound.play_sound("weapon_fire.wav", loop=False)
        if firing_sound:
            # Set source position if sound manager or openal_wrapper has a method
            self.audio_wrapper.set_source_position(firing_sound, *player_pos)

    def reload_weapon(self):
        if not self.equipped_weapon_id:
            self.tts.speak("No weapon equipped.")
            return
        info = self.weapon_data.get_weapon_info(self.equipped_weapon_id)
        ammo_cap = info.get("ammo_capacity",0)
        current_ammo = self.inventory.get_item_quantity(self.equipped_weapon_id)
        if current_ammo < ammo_cap:
            needed = ammo_cap - current_ammo
            self.inventory.add_item(self.equipped_weapon_id, needed)
            self.tts.speak("Weapon reloaded.")
            # Play reload sound, again placed at player position
            player_pos = self.game_state.get("player_position", (0,0,0))
            reload_sound = self.sound.play_sound("weapon_reload.wav", loop=False)
            if reload_sound:
                self.audio_wrapper.set_source_position(reload_sound, *player_pos)
        else:
            self.tts.speak("Weapon is already full.")

    def describe_weapon(self):
        if not self.equipped_weapon_id:
            self.tts.speak("No weapon equipped.")
            return
        info = self.weapon_data.get_weapon_info(self.equipped_weapon_id)
        name = info.get("name","Unknown weapon")
        dmg = info.get("damage","?")
        rng = info.get("range","?")
        self.tts.speak(f"Equipped {name}, damage {dmg}, range {rng}.")

    def on_audio_reinit(self):
        """
        If audio device or volume changes and openal re-init occurs,
        we can reload or adjust sounds here if needed.
        Currently, we rely on sound_manager & audio_wrapper to handle device changes.
        """
        self.logger.debug("Audio reinit detected in WeaponService. No special action needed yet.")
