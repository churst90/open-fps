class ScreenManager:
    def __init__(self):
        self.screens = {}
        self.screen_stack = []
        self.default_screen = None  # Add a default screen

    def add_screen(self, screen, screen_name):
        self.screens[screen_name] = screen
        if self.default_screen is None:  # Set the first added screen as default
            self.default_screen = screen_name

    def push_screen(self, screen_name):
        if screen_name in self.screens:
            self.screen_stack.append(screen_name)
        else:
            print(f"Error: screen '{screen_name}' does not exist.")

    def pop_screen(self):
        if self.screen_stack:
            self.screen_stack.pop()
        else:
            print("Error: no screen to pop.")

    def remove_screen(self, screen_name):
        if screen_name in self.screens:
            del self.screens[screen_name]
            self.screen_stack = [screen for screen in self.screen_stack if screen != screen_name]
        else:
            print(f"Error: screen '{screen_name}' does not exist.")

    def get_current_screen(self):
        if self.screen_stack:
            return self.screens[self.screen_stack[-1]]
        else:  # Return the default screen if the stack is empty
            print("Warning: no screen on the stack. Returning default screen.")
            return self.screens[self.default_screen]

    def screen_exists(self, screen_name):
        return screen_name in self.screens

    def update(self):
        if self.screen_stack:
            self.screens[self.screen_stack[-1]].update()
        elif self.default_screen:  # If there is a default screen, update it
            self.screens[self.default_screen].update()