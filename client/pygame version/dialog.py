import asyncio
from gamewindow import GameWindow
from textfield import TextField

class Dialog:
    def __init__(self, title, screen_manager, field_names, network, tts):
        self.title = title.replace(" ", "_")
        self.screen_manager = screen_manager
        self.network = network
        self.tts = tts
        self.fields = {name: TextField() for name in field_names}
        self.current_field = next(iter(self.fields))  # Start with the first field
        self.dialog_screen = GameWindow(600, 600, title, self.screen_manager)
        self.setup_dialog()

    def setup_dialog(self):
        # Setup dialog window, background, etc.
        self.dialog_screen.set_background((175, 175, 175))
        self.screen_manager.add_screen(self.dialog_screen, f"{self.title}_screen")
        self.screen_manager.push_screen(f"{self.title}_screen")

    async def handle_dialog(self):
        while True:
            events = self.dialog_screen.handle_events()
            if "QUIT" in events:
                break
            await self.process_events(events)
        self.cleanup_dialog()
        return self.collect_input()

    async def process_events(self, events):
        if "KEYDOWN" in events:
            event_key = events["KEYDOWN"]
            if event_key == self.dialog_screen.K_TAB:
                self.switch_field()
            elif event_key == self.dialog_screen.K_RETURN:
                return
            elif event_key == self.dialog_screen.K_BACKSPACE:
                self.fields[self.current_field].backspace()
        if "CHAR" in events:
            self.fields[self.current_field].append(events["CHAR"])

    def switch_field(self):
        # Cycle through the fields
        field_names = list(self.fields)
        current_index = field_names.index(self.current_field)
        self.current_field = field_names[(current_index + 1) % len(field_names)]
    
    def collect_input(self):
        # Collect data from the fields and return as a dictionary
        data = {field: self.fields[field].get_text() for field in self.fields}
        return {"type": self.title, "fields": data}

    def cleanup_dialog(self):
        # Close and remove the dialog screen
        self.screen_manager.pop_screen()
        self.screen_manager.remove_screen(f"{self.title}_screen")
