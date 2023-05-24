class TextField:
    def __init__(self):
        self.text = ""

    def append(self, character):
        self.text += character

    def backspace(self):
        self.text = self.text[:-1]

    def reset(self):
        self.text = ""

    def get_text(self):
        return self.text
