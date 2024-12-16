# utils/settings_manager.py
import json
import logging
from typing import Optional

class SettingsManager:
    @staticmethod
    def load_settings(filepath: str) -> Optional[dict]:
        """
        Load settings from a JSON file. If the file is missing or invalid, return None.
        """
        logger = logging.getLogger("SettingsManager")
        try:
            with open(filepath, 'r', encoding='utf-8') as f:
                data = json.load(f)
            logger.info(f"Loaded settings from {filepath}.")
            return data
        except FileNotFoundError:
            logger.warning(f"Settings file {filepath} not found.")
            return None
        except json.JSONDecodeError as e:
            logger.error(f"Error decoding JSON from {filepath}: {e}")
            return None

    @staticmethod
    def save_settings(filepath: str, data: dict) -> bool:
        """
        Save settings to a JSON file. Returns True if successful, False otherwise.
        """
        logger = logging.getLogger("SettingsManager")
        try:
            with open(filepath, 'w', encoding='utf-8') as f:
                json.dump(data, f, ensure_ascii=False, indent=4)
            logger.info(f"Saved settings to {filepath}.")
            return True
        except Exception as e:
            logger.error(f"Failed to save settings to {filepath}: {e}")
            return False
