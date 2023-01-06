import random

class Map:
  def __init__(self, name, min_x, max_x, min_y, max_y, min_z, max_z):
    self.name = name
    self.min_x = min_x
    self.max_x = max_x
    self.min_y = min_y
    self.max_y = max_y
    self.min_z = min_z
    self.max_z = max_z
    self.players = {}
    self.tiles=[]
    self.zones = []
    self.items = []
    self.ai = []

  # Create a function for adding tiles to the map
  def addTile(self, min_x, max_x, min_y, max_y, min_z, max_z, type, wall):
    # Add multiple tiles with coordinates within the specified range to the list of tiles
    for x in range(min_x, max_x + 1):
      for y in range(min_y, max_y + 1):
        for z in range(min_z, max_z + 1):
          tile = {"x": x, "y": y, "z": z, "type": type, "wall": wall}
          self.tiles.append(tile)

    # If no matching tile was found, return False
    return False

  # Method to add a zone to the map
  def addZone(self, name, min_x, max_x, min_y, max_y, min_z, max_z):
    zone = {"name": name, "min_x": min_x, "max_x": max_x, "min_y": min_y, "max_y": max_y, "min_z": min_z, "max_z": max_z}
    self.zones.append(zone)

  # Method to add an item to the map
  def addItem(self, item, x, y, z):
    # Check that the item's x, y, and z values are within the bounds of the map
    if x >= self.min_x and x <= self.max_x and y >= self.min_y and y <= self.max_y and z >= self.min_z and z <= self.max_z:
      # Set the item's coordinates
      item.x = x
      item.y = y
      item.z = z

      # Add the item to the list of items on the map
      self.items.append(item)

  # Method to add an AI instance to the map
  def addAI(self, ai):
    # Check that the AI's minimum and maximum x, y, and z values are within the bounds of the map
    if ai.min_x >= self.min_x and ai.min_x <= self.max_x and ai.max_x >= self.min_x and ai.max_x <= self.max_x and ai.min_y >= self.min_y and ai.min_y <= self.max_y and ai.max_y >= self.min_y and ai.max_y <= self.max_y and ai.min_z >= self.min_z and ai.min_z <= self.max_z and ai.max_z >= self.min_z and ai.max_z <= self.max_z:

      # Generate a random x, y, and z coordinate within the specified range
      ai.x = random.randint(ai.min_x, ai.max_x)
      ai.y = random.randint(ai.min_y, ai.max_y)
      ai.z = random.randint(ai.min_z, ai.max_z)

      # Add the AI to the list of AI instances on the map
      self.ai.append(ai)

  def listAI(self):
    for ai in self.ai:
      print(f"AI {ai.name}: ({ai.x}, {ai.y}, {ai.z})")


  def listItems(self):
    for item in self.items:
      print(f"{item.name}: ({item.x}, {item.y}, {item.z})")