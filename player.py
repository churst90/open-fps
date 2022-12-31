from chat import Chat

class Player:
  def __init__(self, username):
    self.username = username
    self.auth_token = None
    self.x = 0
    self.y = 0
    self.z = 0
    self.zone = "unknown area"
    self.direction = 0
    self.health = 10000
    self.energy = 10000
    self.pvp_status = False
    self.rank = None
    self.chats = Chat()
    self.inventory = {}

  def move(self, direction):
    if direction == "left":
      # create the move message
      message = {
      "type": "move",
      "username": self.username,
      "direction": self.direction,
      "value": "left",
      "x": self.x,
      "y": self.y,
      "z": self.z
      }
      #send the message to the server
      return message
    if direction == "right":
      # create the move message
      message = {
      "type": "move",
      "username": self.username,
      "direction": self.direction,
      "value": "right",
      "x": self.x,
      "y": self.y,
      "z": self.z
      }
      #send the message to the server
      return message
    if direction == "backward":
      # create the move message
      message = {
      "type": "move",
      "username": self.username,
      "direction": self.direction,
      "value": "backward",
      "x": self.x,
      "y": self.y,
      "z": self.z
      }
      #send the message to the server
      return message
    if direction == "forward":
      # create the move message
      message = {
      "type": "move",
      "username": self.username,
      "direction": self.direction,
      "value": "forward",
      "x": self.x,
      "y": self.y,
      "z": self.z
      }
      #send the message to the server
      return message
    if direction == "up":
      # create the move message
      message = {
      "type": "move",
      "username": self.username,
      "direction": self.direction,
      "value": "up",
      "x": self.x,
      "y": self.y,
      "z": self.z
      }
      #send the message to the server
      return message
    if direction == "down":
      # create the move message
      message = {
      "type": "move",
     "auth_token": self.auth_token,
      "username": self.username,
      "direction": self.direction,
      "value": "down",
      "x": self.x,
      "y": self.y,
      "z": self.z
      }
      #send the message to the server
      return message

  def turn(self, direction):
      # create the turn message
      message = {
      "type": "turn",
      "username": self.username,
      "value": direction,
      "direction": 0
      }
      #send the message to the server
      return message
