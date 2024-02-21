import asyncio
import tkinter as tk
import logging

from speech_manager import SpeechManager
from network import Network
from login_dialog import LoginDialog
from create_account_dialog import CreateAccountDialog
from client_handler import ClientHandler

class Client:
    def __init__(self, host, port):
        self.host = host
        self.port = port
        self.root = tk.Tk()
        self.root.title("Open FPS client")
        self.tts = SpeechManager()
        self.logger = logging.getLogger("client")
        self.network = Network(self.logger)
        self.network.set_host(host)
        self.network.set_port(port)
        self.client_handler = ClientHandler(self.network)
        self.initialize_tkinter()
#        asyncio.run(self.client_handler.start_processing())

    def initialize_tkinter(self):
        self.root.geometry("1200x800")
        self.setup_startup_menu()

    def setup_startup_menu(self):
        self.startup_frame = tk.Frame(self.root)
        self.menu_listbox = tk.Listbox(self.startup_frame, height=4, exportselection=0, activestyle='dotbox')
        options = ["Login", "Create Account", "Exit"]
        for option in options:
            self.menu_listbox.insert(tk.END, option)
        self.menu_listbox.pack(pady=20)
        self.menu_listbox.bind('<Return>', self.on_menu_select)
        self.menu_listbox.bind('<<ListboxSelect>>', self.on_menu_focus)
        self.menu_listbox.focus_set()
        self.startup_frame.pack()

    def on_menu_select(self, event=None):
        selection = self.menu_listbox.get(self.menu_listbox.curselection())
        if selection == "Login":
            self.show_login_dialog()
        elif selection == "Create Account":
            self.show_create_account_dialog()
        elif selection == "Exit":
            self.root.quit()

    def on_menu_focus(self, event=None):
        selection = self.menu_listbox.get(self.menu_listbox.curselection())
        self.tts.speak(selection)

    def show_login_dialog(self):
        self.startup_frame.pack_forget()  # Hide the startup menu
        LoginDialog(self.root, self.tts, self.show_startup_menu_again, self.client_handler)

    def show_create_account_dialog(self):
        self.startup_frame.pack_forget()  # Hide the startup menu
        self.tts.speak("Create account dialog opened.")
        CreateAccountDialog(self.root, self.tts, self.show_startup_menu_again)

    def show_startup_menu_again(self):
        self.startup_frame.pack()  # Re-show the startup menu when login dialog is closed or cancelled

    def main(self):
        self.root.mainloop()

if __name__ == '__main__':
    logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(name)s - %(levelname)s - %(message)s')
    client = Client("localhost", 33288)
    client.main()
