from gamewindow import GameWindow as gw
from speechmanager import SpeechManager

class Menu:
    def __init__(self, options, title, screen_id, screen_manager):
        self.screen_manager = screen_manager
        self.options = options
        self.title = title
        self.screen_id = screen_id
        self.selected = 0
        self.tts = SpeechManager()

    async def create(self):
        menu_screen = gw(300, 500, self.title, self.screen_manager)
        menu_screen.set_background((175, 175, 175))
        self.screen_manager.add_screen(menu_screen, self.screen_id)
        self.screen_manager.push_screen(self.screen_id)
        while True:
            events = menu_screen.handle_events()

            if "QUIT" in events:
                return

            if "KEYDOWN" in events:
                event = events["KEYDOWN"]
                if event == menu_screen.K_UP:
                    self.selected = max(self.selected - 1, 0)
                    self.tts.speak(self.options[self.selected])
                elif event == menu_screen.K_DOWN:
                    self.selected = min(self.selected + 1, len(self.options) - 1)
                    self.tts.speak(self.options[self.selected])
                elif event == menu_screen.K_RETURN:
                    return self.options[self.selected]

            self.screen_manager.update()
            menu_screen.update()
        self.screen_manager.pop_screen()