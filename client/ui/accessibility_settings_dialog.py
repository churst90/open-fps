# ui/accessibility_settings_dialog.py
import tkinter as tk
import logging
from utils.settings_manager import SettingsManager
from ui.base_dialog import BaseDialog

class AccessibilitySettingsDialog(BaseDialog):
    """
    Enhanced: after user presses OK, we call tts_manager.reinit_tts()
    and if openal_wrapper instance is available (passed in?), reinit_audio().
    We'll assume we can access sound_manager and tts_manager from the main app context.
    """

    def __init__(self, master, tts_manager, sound_manager, audio_wrapper):
        # We add `audio_wrapper` as a parameter so we can reinit audio too.
        super().__init__(master, tts_manager, sound_manager, title="Accessibility Settings")

        self.logger = logging.getLogger("AccessibilitySettingsDialog")
        self.result = False
        self.audio_wrapper = audio_wrapper

        settings_file = "config/settings.json"
        self.settings = SettingsManager.load_settings(settings_file) or {}
        speech_cfg = self.settings.get("speech", {"use_nvda": False, "rate":200,"volume":1.0,"voice":""})

        self.use_nvda_var = tk.BooleanVar(value=speech_cfg.get("use_nvda",False))
        self.rate_var = tk.IntVar(value=speech_cfg.get("rate",200))
        self.volume_var = tk.DoubleVar(value=speech_cfg.get("volume",1.0))
        self.voice_var = tk.StringVar(value=speech_cfg.get("voice",""))

        tk.Label(self.top, text="Use NVDA (Windows only):").pack(pady=5)
        nvda_check = tk.Checkbutton(self.top, text="Enable NVDA if available", variable=self.use_nvda_var)
        nvda_check.pack(pady=5)

        tk.Label(self.top, text="Speech Rate:").pack(pady=5)
        rate_spin = tk.Spinbox(self.top, from_=50, to=400, increment=10, textvariable=self.rate_var)
        rate_spin.pack(pady=5)

        tk.Label(self.top, text="Volume (0.0 to 1.0):").pack(pady=5)
        vol_spin = tk.Spinbox(self.top, from_=0.0, to=1.0, increment=0.1, textvariable=self.volume_var)
        vol_spin.pack(pady=5)

        tk.Label(self.top, text="Voice (optional substring):").pack(pady=5)
        voice_entry = tk.Entry(self.top, textvariable=self.voice_var)
        voice_entry.pack(pady=5)

        # Assume we add a field for preferred audio device:
        tk.Label(self.top, text="Preferred Audio Device:").pack(pady=5)
        self.device_entry = tk.Entry(self.top)
        self.device_entry.pack(pady=5)
        audio_cfg = self.settings.get("audio", {"volume":1.0})
        preferred_device = audio_cfg.get("preferred_device", "")
        self.device_entry.insert(0, preferred_device)

        btn_frame = tk.Frame(self.top)
        btn_frame.pack(pady=10)
        ok_btn = tk.Button(btn_frame, text="OK", command=self.on_ok)
        ok_btn.pack(side="left", padx=5)
        cancel_btn = tk.Button(btn_frame, text="Cancel", command=self.on_cancel)
        cancel_btn.pack(side="left", padx=5)

        self.top.bind("<Escape>", self.on_cancel)

        self.speak_intro("Accessibility settings dialog opened. Adjust NVDA usage, speech rate, volume, voice, and audio device, then press OK.")

        self.bind_focus_in(nvda_check, "NVDA checkbox. Check to enable NVDA if available.")
        self.bind_focus_in(rate_spin, f"Speech rate spinner. Current {self.rate_var.get()} words per minute.")
        self.bind_focus_in(vol_spin, f"Volume spinner. Current {self.volume_var.get()}.")
        self.bind_focus_in(voice_entry, "Voice field. Type a substring of voice name or leave blank.")
        self.bind_focus_in(self.device_entry, "Preferred audio device field. Type device name or leave blank.")
        self.bind_focus_in(ok_btn, "OK button. Press enter to save changes.")
        self.bind_focus_in(cancel_btn, "Cancel button. Press enter to discard changes.")

        nvda_check.focus_set()
        self.sound.play_menu_sound("menu_click.wav")

    def on_ok(self):
        if "speech" not in self.settings:
            self.settings["speech"] = {}
        self.settings["speech"]["use_nvda"] = self.use_nvda_var.get()
        self.settings["speech"]["rate"] = self.rate_var.get()
        self.settings["speech"]["volume"] = self.volume_var.get()
        self.settings["speech"]["voice"] = self.voice_var.get().strip()

        if "audio" not in self.settings:
            self.settings["audio"] = {}
        self.settings["audio"]["preferred_device"] = self.device_entry.get().strip()

        if SettingsManager.save_settings("config/settings.json", self.settings):
            self.tts.speak("Accessibility settings saved. Reinitializing speech and audio.")
            # Reinit TTS to apply new NVDA or voice settings
            self.tts.reinit_tts()
            # Reinit audio if supported
            if self.audio_wrapper:
                self.audio_wrapper.reinit_audio()

            self.result = True
        else:
            self.tts.speak("Failed to save accessibility settings.")
        self.on_close()

    def on_cancel(self, event=None):
        self.tts.speak("Canceling, no changes saved.")
        self.on_close()
