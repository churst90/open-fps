# ui/create_account_dialog.py
import tkinter as tk
import logging

class CreateAccountDialog:
    """
    Create account dialog with TTS announcements and tone feedback on focus.
    """

    def __init__(self, master, tts_manager, audio_service):
        self.logger = logging.getLogger("CreateAccountDialog")
        self.tts = tts_manager
        self.audio_service = audio_service

        self.top = tk.Toplevel(master)
        self.top.title("Create Account")
        self.top.grab_set()
        self.result = None

        tk.Label(self.top, text="Username:").pack(pady=5)
        self.username_entry = tk.Entry(self.top)
        self.username_entry.pack(pady=5)
        self.username_entry.focus_set()

        tk.Label(self.top, text="Password:").pack(pady=5)
        self.password_entry = tk.Entry(self.top, show="*")
        self.password_entry.pack(pady=5)

        tk.Label(self.top, text="Role (optional):").pack(pady=5)
        self.role_entry = tk.Entry(self.top)
        self.role_entry.insert(0, "player")
        self.role_entry.pack(pady=5)

        btn_frame = tk.Frame(self.top)
        btn_frame.pack(pady=10)
        ok_btn = tk.Button(btn_frame, text="OK", command=self.on_ok)
        ok_btn.pack(side="left", padx=5)
        cancel_btn = tk.Button(btn_frame, text="Cancel", command=self.on_cancel)
        cancel_btn.pack(side="left", padx=5)

        self.top.bind("<Return>", self.on_ok)
        self.top.bind("<Escape>", self.on_cancel)

        self.tts.interrupt_speech()
        self.tts.speak("Create account dialog opened. Enter username, password, and optional role, then press OK.")
        self.audio_service.sound_manager.audio.play_tone(frequency=1000, duration=0.05, volume=0.5)

        # <FocusIn> events
        self.username_entry.bind("<FocusIn>", self.on_focus_username)
        self.password_entry.bind("<FocusIn>", self.on_focus_password)
        self.role_entry.bind("<FocusIn>", self.on_focus_role)
        ok_btn.bind("<FocusIn>", self.on_focus_ok)
        cancel_btn.bind("<FocusIn>", self.on_focus_cancel)

    def on_focus_username(self, event):
        self.tts.interrupt_speech()
        self.tts.speak("Username field. Type your desired username.")
        self.audio_service.sound_manager.audio.play_tone(frequency=880, duration=0.05, volume=0.5)

    def on_focus_password(self, event):
        self.tts.interrupt_speech()
        self.tts.speak("Password field. Type your desired password.")
        self.audio_service.sound_manager.audio.play_tone(frequency=880, duration=0.05, volume=0.5)

    def on_focus_role(self, event):
        self.tts.interrupt_speech()
        self.tts.speak("Role field. Default is player. You may leave it or type another role.")
        self.audio_service.sound_manager.audio.play_tone(frequency=880, duration=0.05, volume=0.5)

    def on_focus_ok(self, event):
        self.tts.interrupt_speech()
        self.tts.speak("OK button. Press enter to create account.")
        self.audio_service.sound_manager.audio.play_tone(frequency=880, duration=0.05, volume=0.5)

    def on_focus_cancel(self, event):
        self.tts.interrupt_speech()
        self.tts.speak("Cancel button. Press enter to cancel and return.")
        self.audio_service.sound_manager.audio.play_tone(frequency=880, duration=0.05, volume=0.5)

    def on_ok(self, event=None):
        username = self.username_entry.get().strip()
        password = self.password_entry.get().strip()
        role = self.role_entry.get().strip() or "player"
        if username and password:
            self.result = (username, password, role)
            self.tts.interrupt_speech()
            self.tts.speak("Creating account.")
            self.audio_service.sound_manager.audio.play_tone(frequency=660, duration=0.1, volume=0.5)
            self.top.destroy()
        else:
            self.tts.interrupt_speech()
            self.tts.speak("Username or password empty, please try again.")
            self.audio_service.sound_manager.audio.play_tone(frequency=440, duration=0.1, volume=0.5)

    def on_cancel(self, event=None):
        self.tts.interrupt_speech()
        self.tts.speak("Canceling account creation.")
        self.audio_service.sound_manager.audio.play_tone(frequency=440, duration=0.1, volume=0.5)
        self.top.destroy()
