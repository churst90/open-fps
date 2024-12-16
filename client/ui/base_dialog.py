# ui/base_dialog.py
import tkinter as tk
import logging
from ui.window_manager import WindowManager

class BaseDialog:
    """
    A base class for dialogs that ensures:
    - Registration with WindowManager
    - Non-blocking TTS announcements on open
    - Focus management on close
    - A consistent place to bind <FocusIn> events in derived classes
    """

    def __init__(self, master, tts_manager, sound_manager, title="Dialog"):
        self.logger = logging.getLogger(self.__class__.__name__)
        self.tts = tts_manager
        self.sound = sound_manager
        self.top = tk.Toplevel(master)
        self.top.title(title)
        self.top.grab_set()  # modal

        # Register this dialog with WindowManager
        wm = WindowManager.get_instance()
        if wm:
            wm.register_dialog(self)

        # When dialog closes
        self.top.protocol("WM_DELETE_WINDOW", self.on_close)

    def on_close(self):
        """
        Called when user closes the dialog.
        Unregister from WindowManager and destroy.
        """
        wm = WindowManager.get_instance()
        if wm:
            wm.unregister_dialog(self)
        self.top.destroy()

    def speak_intro(self, message: str):
        """
        Speak an introductory message when the dialog is opened.
        """
        self.tts.speak(message)

    def bind_focus_in(self, widget, description):
        """
        A helper method to bind <FocusIn> event on a widget and announce `description`.
        """
        widget.bind("<FocusIn>", lambda e: self.tts.speak(description))
