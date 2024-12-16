# ui/keybindings_dialog.py
import tkinter as tk
import logging
from tkinter import messagebox
from typing import Dict, Any
from utils.keybinding_manager import KeybindingManager

class KeybindingsDialog:
    """
    A dialog to view and modify keybindings.
    Shows a list of actions and their keys. User can select an action and press a new key to rebind it.
    On OK, saves changes to keybindings.json.
    """

    def __init__(self, master, tts_manager, sound_manager, keybinding_manager: KeybindingManager):
        self.logger = logging.getLogger("KeybindingsDialog")
        self.tts = tts_manager
        self.sound = sound_manager
        self.keybinds = keybinding_manager
        self.result = False

        self.top = tk.Toplevel(master)
        self.top.title("Keybindings")
        self.top.grab_set()

        tk.Label(self.top, text="Keybindings:", font=("Arial",14)).pack(pady=5)
        self.listbox = tk.Listbox(self.top, font=("Arial",12), height=10)
        self.listbox.pack(pady=5)

        self.actions = list(self.keybinds.bindings.keys())
        for action in self.actions:
            key = self.keybinds.bindings[action]
            self.listbox.insert(tk.END, f"{action}: {key}")

        btn_frame = tk.Frame(self.top)
        btn_frame.pack(pady=10)
        rebind_btn = tk.Button(btn_frame, text="Rebind", command=self.on_rebind)
        rebind_btn.pack(side="left", padx=5)
        ok_btn = tk.Button(btn_frame, text="OK", command=self.on_ok)
        ok_btn.pack(side="left", padx=5)
        cancel_btn = tk.Button(btn_frame, text="Cancel", command=self.on_cancel)
        cancel_btn.pack(side="left", padx=5)

        self.top.bind("<Escape>", self.on_cancel)
        self.tts.speak("Keybindings dialog opened. Select an action and press rebind to assign a new key.")

    def on_rebind(self):
        selection = self.listbox.curselection()
        if not selection:
            self.tts.speak("No action selected for rebind.")
            return
        index = selection[0]
        action = self.actions[index]

        self.tts.speak(f"Press a new key for {action}. After pressing a key, this dialog will update.")
        self.sound.play_menu_sound("menu_click.wav")

        # Wait for a single key press
        # We'll open a small modal window and bind a single key press to capture it.
        capture = KeyCaptureDialog(self.top, self.tts, self.sound)
        self.top.wait_window(capture.top)
        if capture.result_key:
            new_key = capture.result_key
            if self.keybinds.set_binding(action, new_key):
                self.update_listbox()
                self.tts.speak(f"Rebound {action} to {new_key}.")
            else:
                self.tts.speak("Failed to rebind action.")

    def on_ok(self):
        if self.keybinds.save_keybindings():
            self.tts.speak("Keybindings saved.")
            self.result = True
        else:
            self.tts.speak("Failed to save keybindings.")
        self.top.destroy()

    def on_cancel(self, event=None):
        self.tts.speak("Canceling, no changes saved.")
        self.top.destroy()

    def update_listbox(self):
        self.listbox.delete(0, tk.END)
        for action in self.actions:
            key = self.keybinds.bindings[action]
            self.listbox.insert(tk.END, f"{action}: {key}")

class KeyCaptureDialog:
    """
    A small modal dialog to capture a single key press.
    """

    def __init__(self, master, tts, sound):
        self.tts = tts
        self.sound = sound
        self.result_key = None
        self.top = tk.Toplevel(master)
        self.top.title("Press a Key")
        self.top.grab_set()

        tk.Label(self.top, text="Press any key...").pack(pady=20)
        self.top.bind("<Key>", self.on_key)
        self.tts.speak("Press any key now.")

    def on_key(self, event):
        self.result_key = event.keysym
        self.sound.play_menu_sound("menu_click.wav")
        self.tts.speak(f"Captured {self.result_key}")
        self.top.destroy()
