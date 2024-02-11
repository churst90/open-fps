import random
import math

class AI:
  def __init__(self, type, name, min_x, max_x, min_y, max_y, min_z, max_z, speed, health, damage):
    self.type = type
    self.name = name
    self.min_x = min_x
    self.max_x = max_x
    self.min_y = min_y
    self.max_y = max_y
    self.min_z = min_z
    self.max_z = max_z
    self.x = None
    self.y = None
    self.z = None

  # Method to define the behavior of an AI instance
  def behavior(self, ai, target):
    # Calculate the distance and direction to the nearest player or target using Euclidean geometry
    dx = target["x"] - ai["x"]
    dy = target["y"] - ai["y"]
    dz = target["z"] - ai["z"]
    distance = math.sqrt(dx**2 + dy**2 + dz**2)
    direction = (dx / distance, dy / distance, dz / distance)

    # Use the distance and direction to move the AI toward the target
    new_x = ai["x"] + direction[0] * ai["speed"]
    new_y = ai["y"] + direction[1] * ai["speed"]
    new_z = ai["z"] + direction[2] * ai["speed"]

    # Update the AI's position and state based on its calculated behavior
    ai["x"] = new_x
    ai["y"] = new_y
    ai["z"] = new_z
    ai["health"] = new_health

  # Method to update the positions of all AI instances on all maps
  def updateAI(self, maps):
    for map in maps.values():
      for ai in map.ai:
        # Call the behavior function for the AI to update its position
        self.behavior(ai, map.players)

  def countAI(self, maps):
    count = 0
    for map in maps.values():
      count += len(map.ai)
    return count
