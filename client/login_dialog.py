import tkinter as tk
import asyncio

class LoginDialog:
    def __init__(self, parent, tts, on_cancel, client_handler):
        self.client_handler = client_handler
        self.tts = tts
        self.dialog = tk.Toplevel(parent)
        self.dialog.title("Login dialog")
        self.dialog.geometry("300x200")
        self.dialog.protocol("WM_DELETE_WINDOW", self.cancel_login)  # Handle dialog close button

        # Username field with enhanced focus event for accessibility
        tk.Label(self.dialog, text="Username:").pack(pady=(10, 0))
        self.username_entry = tk.Entry(self.dialog)
        self.username_entry.pack(pady=(0, 10))
        self.username_entry.bind("<FocusIn>", lambda e: tts.speak("Username field. Edit: " + self.username_entry.get()))

        # Password field with enhanced focus event for accessibility
        tk.Label(self.dialog, text="Password:").pack(pady=(10, 0))
        self.password_entry = tk.Entry(self.dialog, show="*")
        self.password_entry.pack(pady=(0, 10))
        self.password_entry.bind("<FocusIn>", lambda e: tts.speak("Password field. Edit: " + self.password_entry.get()))

        # Login button with enhanced accessibility
        self.login_button = tk.Button(self.dialog, text="Login", command=self.on_login)
        self.login_button.pack(side=tk.LEFT, padx=(50, 10), pady=20)
        self.login_button.bind("<FocusIn>", lambda e: tts.speak("Login button"))

        # Cancel button with enhanced accessibility
        self.cancel_button = tk.Button(self.dialog, text="Cancel", command=self.cancel_login)
        self.cancel_button.pack(side=tk.RIGHT, padx=(10, 50), pady=20)
        self.cancel_button.bind("<FocusIn>", lambda e: tts.speak("Cancel button"))

        self.on_cancel = on_cancel  # Callback to re-show the startup menu

        self.dialog.grab_set()  # Make the dialog modal
        self.username_entry.focus_set()

    def on_login(self):
        username = self.username_entry.get()
        password = self.password_entry.get()
        asyncio.run(self.client_handler.handle_login(username, password))

        self.dialog.destroy()
        self.on_cancel()  # Re-show the startup menu after login attempt

    def cancel_login(self):
        self.dialog.destroy()
        self.on_cancel()  # Ensure startup menu reappears if login is cancelled
