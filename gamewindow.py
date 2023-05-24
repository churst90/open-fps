import pygame

class GameWindow:
  KEYDOWN = pygame.KEYDOWN
  KEYUP = pygame.KEYUP
  K_RETURN = pygame.K_RETURN
  K_TAB = pygame.K_TAB
  K_BACKSPACE = pygame.K_BACKSPACE
  K_LEFT = pygame.K_LEFT
  K_RIGHT = pygame.K_RIGHT
  K_DOWN = pygame.K_DOWN
  K_UP = pygame.K_UP

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
    if self.screen_manager.get_current_screen():
      pygame.display.update()
      self.clock.tick(60)

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

  def get_size(self):
    return self.width, self.height

  def get_clock(self):
    return self.clock

  def handle_events(self):
    game_events = []
    text_events = []

    events = pygame.event.get()

    for event in events:
      if event.type == pygame.QUIT:
        game_events.append(event)
      elif event.type == pygame.KEYDOWN or event.type == pygame.KEYUP:
        game_events.append(event)
        if event.key == pygame.K_BACKSPACE:
          game_events.append(event)
        elif event.key < 256 and event.unicode.isprintable():
          text_events.append(str(event.unicode))  # Convert character to string

    return {"game": game_events, "text": text_events}
