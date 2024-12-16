# ui/server_list_dialog.py
import tkinter as tk
import logging
from tkinter import simpledialog, messagebox
from utils.settings_manager import SettingsManager
from ui.base_dialog import BaseDialog

class ServerListDialog(BaseDialog):
    """
    Revised ServerListDialog to use BaseDialog, improving accessibility and window management.
    """

    def __init__(self, master, tts_manager, sound_manager):
        super().__init__(master, tts_manager, sound_manager, title="Server List")
        self.logger = logging.getLogger("ServerListDialog")
        self.result = False

        self.servers_file = "config/servers.json"
        self.servers = SettingsManager.load_settings(self.servers_file) or []

        tk.Label(self.top, text="Servers:", font=("Arial",14)).pack(pady=5)
        self.server_listbox = tk.Listbox(self.top, font=("Arial",12), height=10)
        self.server_listbox.pack(pady=5)

        self._load_servers_into_listbox()

        btn_frame = tk.Frame(self.top)
        btn_frame.pack(pady=10)

        add_btn = tk.Button(btn_frame, text="Add", command=self.on_add)
        add_btn.pack(side="left", padx=5)
        edit_btn = tk.Button(btn_frame, text="Edit", command=self.on_edit)
        edit_btn.pack(side="left", padx=5)
        remove_btn = tk.Button(btn_frame, text="Remove", command=self.on_remove)
        remove_btn.pack(side="left", padx=5)

        bottom_frame = tk.Frame(self.top)
        bottom_frame.pack(pady=10)
        ok_btn = tk.Button(bottom_frame, text="OK", command=self.on_ok)
        ok_btn.pack(side="left", padx=5)
        cancel_btn = tk.Button(bottom_frame, text="Cancel", command=self.on_cancel)
        cancel_btn.pack(side="left", padx=5)

        self.top.bind("<Escape>", self.on_cancel)

        # Introduce the dialog
        self.speak_intro("Server list dialog opened. Use arrow keys to select a server, or add, edit, remove them. Tab for buttons.")

        # Bind focus for accessibility
        self.bind_focus_in(self.server_listbox, "Servers list. Use arrow keys to navigate.")
        self.bind_focus_in(add_btn, "Add button. Press enter to add a new server.")
        self.bind_focus_in(edit_btn, "Edit button. Press enter to edit the selected server.")
        self.bind_focus_in(remove_btn, "Remove button. Press enter to remove the selected server.")
        self.bind_focus_in(ok_btn, "OK button. Press enter to save changes.")
        self.bind_focus_in(cancel_btn, "Cancel button. Press enter to discard changes.")

        self.server_listbox.focus_set()
        self.sound.play_menu_sound("menu_click.wav")

    def _load_servers_into_listbox(self):
        self.server_listbox.delete(0, tk.END)
        for srv in self.servers:
            display = f"{srv.get('name','Unnamed')} ({srv.get('host','?')}:{srv.get('port','?')})"
            self.server_listbox.insert(tk.END, display)

    def on_add(self):
        self.tts.speak("Adding a new server.")
        host = simpledialog.askstring("Host", "Enter host:", parent=self.top)
        if host is None:
            return
        port_str = simpledialog.askstring("Port", "Enter port:", parent=self.top)
        if port_str is None:
            return
        try:
            port = int(port_str)
        except ValueError:
            self.tts.speak("Invalid port.")
            return
        name = simpledialog.askstring("Name", "Enter a friendly name:", parent=self.top)
        if name is None:
            name = "Unnamed Server"
        self.servers.append({"host": host, "port": port, "name": name})
        self._load_servers_into_listbox()
        self.tts.speak(f"Server {name} added.")

    def on_edit(self):
        selection = self.server_listbox.curselection()
        if not selection:
            self.tts.speak("No server selected for edit.")
            return
        index = selection[0]
        srv = self.servers[index]
        self.tts.speak("Editing selected server.")
        host = simpledialog.askstring("Host", "Enter host:", parent=self.top, initialvalue=srv.get("host",""))
        if host is None:
            return
        port_str = simpledialog.askstring("Port", "Enter port:", parent=self.top, initialvalue=str(srv.get("port","")))
        if port_str is None:
            return
        try:
            port = int(port_str)
        except ValueError:
            self.tts.speak("Invalid port.")
            return
        name = simpledialog.askstring("Name", "Enter a friendly name:", parent=self.top, initialvalue=srv.get("name","Unnamed"))
        if name is None:
            return
        srv["host"] = host
        srv["port"] = port
        srv["name"] = name
        self._load_servers_into_listbox()
        self.tts.speak(f"Server {name} updated.")

    def on_remove(self):
        selection = self.server_listbox.curselection()
        if not selection:
            self.tts.speak("No server selected for removal.")
            return
        index = selection[0]
        srv = self.servers[index]
        if messagebox.askyesno("Remove", f"Remove server {srv.get('name')}?"):
            removed_name = srv.get("name","Unnamed")
            self.servers.pop(index)
            self._load_servers_into_listbox()
            self.tts.speak(f"Server {removed_name} removed.")

    def on_ok(self):
        from utils.settings_manager import SettingsManager
        import os
        if not os.path.exists("config"):
            os.makedirs("config")
        if SettingsManager.save_settings(self.servers_file, self.servers):
            self.tts.speak("Servers saved.")
            self.result = True
        else:
            self.tts.speak("Failed to save servers.")
        self.on_close()

    def on_cancel(self, event=None):
        self.tts.speak("Canceling, no changes saved.")
        self.on_close()
