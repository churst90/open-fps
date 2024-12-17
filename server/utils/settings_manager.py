# utils/settings_manager.py
import json
import os

class SettingsManager:
    def __init__(self, settings_file='config/settings.json'):
        self.settings_file = settings_file
        self.settings = {}

    def load_settings(self):
        if os.path.exists(self.settings_file):
            with open(self.settings_file, 'r', encoding='utf-8') as f:
                self.settings = json.load(f)
        else:
            self.settings = {}

    def save_settings(self):
        with open(self.settings_file, 'w', encoding='utf-8') as f:
            json.dump(self.settings, f, indent=4)

    def get(self, key, default=None):
        parts = key.split('.')
        val = self.settings
        for p in parts:
            if p in val:
                val = val[p]
            else:
                return default
        return val

    def set(self, key, value):
        parts = key.split('.')
        d = self.settings
        for p in parts[:-1]:
            if p not in d:
                d[p] = {}
            d = d[p]
        d[parts[-1]] = value

global_settings = SettingsManager()
