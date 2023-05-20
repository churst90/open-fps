from gamewindow import GameWindow as gw
from speechmanager import SpeechManager

class Menu:
    def __init__(self, options, title, screen_manager):
        self.screen_manager = screen_manager
        self.options = options
        self.title = title
        self.selected = 0
        self.tts = SpeechManager()

    def create(self):
        menu = gw(800, 600, self.title, self.screen_manager)
        self.screen_manager.add_screen(menu)
        self.screen_manager.set_active_screen(menu)
        while True:
            menu.update()
            events = menu.handle_events()
            for event in events:
                if event.type == menu.KEYDOWN:
                    if event.key == menu.K_UP:
                        self.selected = max(self.selected - 1, 0)
                        self.tts.speak(self.options[self.selected])
                    elif event.key == menu.K_DOWN:
                        self.selected = min(self.selected + 1, len(self.options) - 1)
                        self.tts.speak(self.options[self.selected])
                    elif event.key == menu.K_RETURN:
                        menu.close()
                        return self.options[self.selected]
