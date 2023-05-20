import pygame

class GameWindow:
  # Define key constants as class attributes
  KEYDOWN = pygame.KEYDOWN
  K_UP = pygame.K_UP
  K_DOWN = pygame.K_DOWN
  K_RETURN = pygame.K_RETURN
  K_TAB = pygame.K_TAB
  K_BACKSPACE = pygame.K_BACKSPACE
  def __init__(self, width, height, title, screen_manager):
    self.screen_manager = screen_manager
    self.width = width
    self.height = height
    self.title = title

    pygame.init()
    self.window = pygame.display.set_mode((self.width, self.height))
    pygame.display.set_caption(self.title)
    self.clock = pygame.time.Clock()
    self.text_input = ""

  def update(self):
    if self.screen_manager.active_screen:
      pygame.display.update()

  def close(self):
    pygame.quit()

  def set_background(self, color):
    self.window.fill(color)

  def draw_rectangle(self, x, y, width, height, color):
    rect = pygame.Rect(x, y, width, height)
    pygame.draw.rect(self.window, color, rect)

  def draw_circle(self, x, y, radius, color):
    pygame.draw.circle(self.window, color, (x, y), radius)

  def draw_text(self, text, x, y, font_size, color):
    font = pygame.font.SysFont(None, font_size)
    text_surface = font.render(text, True, color)
    self.window.blit(text_surface, (x, y))

  def handle_events(self):
    events = pygame.event.get()
    if pygame.QUIT in [event.type for event in events]:
      return [pygame.event.Event(pygame.QUIT)]  # Return a list with a single quit event
    return events

  def get_size(self):
    return self.width, self.height

  def get_clock(self):
    return self.clock

  def get_text_input(self):
    events = pygame.event.get(pygame.KEYDOWN)
    for event in events:
      if event.key == pygame.K_BACKSPACE:
        self.text_input = self.text_input[:-1]
      elif event.key == pygame.K_RETURN:
        return self.text_input
      elif event.key < 256:
        character = chr(event.key)
        self.text_input += character
    return self.text_input