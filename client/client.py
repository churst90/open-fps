import asyncio
import tkinter as tk
import logging
import threading
from network import Network
from client_handler import ClientHandler
from login_dialog import LoginDialog
from create_account_dialog import CreateAccountDialog
from speech_manager import SpeechManager

class Client:
    def __init__(self, host, port):
        self.host = host
        self.port = port
        self.root = tk.Tk()
        self.root.title("Open FPS Client")
        self.logger = logging.getLogger("client")
        self.loop = asyncio.new_event_loop()
        self.network = Network(self.logger, self.loop)
        self.network.set_host(host)
        self.network.set_port(port)
        self.client_handler = ClientHandler(self.network, self.loop)
        self.tts = SpeechManager()
        self.initialize_tkinter()

    def initialize_tkinter(self):
        self.menu_options = ["Login", "Create Account", "Exit"]
        self.menu_index = 0

        self.menu_label = tk.Label(self.root, text="Use arrow keys to navigate and Enter to select", wraplength=400)
        self.menu_label.pack(pady=(20, 10))

        self.update_menu_display()

        self.root.bind("<Up>", self.navigate_up)
        self.root.bind("<Down>", self.navigate_down)
        self.root.bind("<Return>", self.select_option)

    def update_menu_display(self):
        menu_text = "\n".join([f"[{'X' if i == self.menu_index else ' '}] {option}" for i, option in enumerate(self.menu_options)])
        self.menu_label.config(text=menu_text)
        self.tts.speak(self.menu_options[self.menu_index])

    def navigate_up(self, event):
        self.menu_index = (self.menu_index - 1) % len(self.menu_options)
        self.update_menu_display()

    def navigate_down(self, event):
        self.menu_index = (self.menu_index + 1) % len(self.menu_options)
        self.update_menu_display()

    def select_option(self, event):
        if self.menu_options[self.menu_index] == "Login":
            self.show_login_dialog()
        elif self.menu_options[self.menu_index] == "Create Account":
            self.show_create_account_dialog()
        elif self.menu_options[self.menu_index] == "Exit":
            self.root.quit()

    def show_login_dialog(self):
        self.root.withdraw()
        LoginDialog(self.root, self.tts, self.show_startup_menu_again, self.client_handler)

    def show_create_account_dialog(self):
        self.root.withdraw()
        CreateAccountDialog(self.root, self.tts, self.show_startup_menu_again)

    def show_startup_menu_again(self):
        self.root.deiconify()
        self.update_menu_display()

    def start_asyncio_loop(self):
        threading.Thread(target=self.run_asyncio_loop, daemon=True).start()

    def run_asyncio_loop(self):
        asyncio.set_event_loop(self.loop)
        self.loop.run_forever()

    def main(self):
        self.start_asyncio_loop()
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)
        self.root.mainloop()

    def on_close(self):
        tasks = asyncio.gather(*asyncio.all_tasks(self.loop), return_exceptions=True)
        tasks.cancel()
        self.loop.call_soon_threadsafe(self.loop.stop)
        self.root.destroy()

if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(name)s - %(levelname)s - %(message)s')
    client = Client("localhost", 33288)
    client.main()
