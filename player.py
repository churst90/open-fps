from chat import Chat

class Player:
  def __init__(self, name):
    self.name = name
    self.auth_token = None
    self.x = 0
    self.y = 0
    self.z = 0
    self.health = 10000
    self.energy = 10000
    self.pvp_status = False
    self.rank = None
    self.chats = Chat()
    self.inventory = {}