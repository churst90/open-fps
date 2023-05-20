class ScreenManager:
    def __init__(self):
        self.screens = []
        self.active_screen = None

    def add_screen(self, screen):
        self.screens.append(screen)

    def set_active_screen(self, screen):
        self.active_screen = screen

    def update(self):
        self.active_screen.update()
