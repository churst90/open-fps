from gamewindow import GameWindow as gw
from speechmanager import SpeechManager

class Menu:
    def __init__(self, options, title, screen_manager):
        self.screen_manager = screen_manager
        self.game_window = gw(800, 600, title, self.screen_manager)
        self.options = options
        self.selected = 0
        self.tts = SpeechManager()

    def create(self):
        while True:
            events = self.game_window.handle_events()
            for event in events:
                if event.type == self.game_window.KEYDOWN:
                    if event.key == self.game_window.K_UP:
                        self.selected = max(self.selected - 1, 0)
                        self.tts.speak(self.options[self.selected])
                    elif event.key == self.game_window.K_DOWN:
                        self.selected = min(self.selected + 1, len(self.options) - 1)
                        self.tts.speak(self.options[self.selected])
                    elif event.key == self.game_window.K_RETURN:
                        self.game_window.close()
                        return self.options[self.selected]
