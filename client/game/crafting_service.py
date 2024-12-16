# game/crafting_service.py
import logging
from typing import Dict, Any
from game.crafting_data import CraftingData
from game.inventory_manager import InventoryManager
from speech.tts_manager import TTSManager
from audio.sound_manager import SoundManager

class CraftingService:
    """
    Phase 3 Enhancements:
    - Interrupt speech before announcing new crafting steps if user navigates rapidly.
    - If user changes TTS or audio settings, no direct action is required here, but we show that 
      calls to tts.reinit_tts() or audio_wrapper.reinit_audio() could be done if needed in future scenarios.
    """

    def __init__(self, crafting_data: CraftingData, inventory: InventoryManager, tts: TTSManager, sound: SoundManager, game_state: Dict[str,Any], audio_wrapper):
        self.logger = logging.getLogger("CraftingService")
        self.crafting_data = crafting_data
        self.inventory = inventory
        self.tts = tts
        self.sound = sound
        self.game_state = game_state
        self.audio_wrapper = audio_wrapper

    def can_craft(self, recipe_id: str) -> bool:
        self.tts.interrupt_speech()
        recipe = self.crafting_data.get_recipe_info(recipe_id)
        if not recipe:
            self.tts.speak("I don't know that recipe.")
            return False
        ingredients = recipe.get("ingredients", {})
        for item_id, qty in ingredients.items():
            have = self.inventory.get_item_quantity(item_id)
            if have < qty:
                self.tts.speak(f"Not enough {item_id}. Need {qty}, have {have}.")
                return False
        return True

    def craft_item(self, recipe_id: str) -> bool:
        if not self.can_craft(recipe_id):
            return False
        recipe = self.crafting_data.get_recipe_info(recipe_id)
        ingredients = recipe.get("ingredients", {})
        result_item_id = recipe.get("result_item_id", None)
        if not result_item_id:
            self.tts.speak("Recipe has no result item, cannot craft.")
            return False

        # Remove ingredients, add result
        for item_id, qty in ingredients.items():
            self.inventory.remove_item(item_id, qty)
        self.inventory.add_item(result_item_id, 1)

        self.tts.interrupt_speech()
        self.tts.speak(f"Crafted {result_item_id}.")
        self.sound.play_menu_sound("menu_click.wav")
        return True

    def on_audio_reinit(self):
        """
        If audio changes at runtime, no special action needed for crafting currently.
        Future enhancements might adjust crafting sounds or effects if we implement them.
        """
        self.logger.debug("Audio reinit in CraftingService. No special action currently.")
