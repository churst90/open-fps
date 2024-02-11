class Collision:
  def __init__(self, map):
    self.map = map

  # Method to check if a player's proposed movement will collide with a wall
  def check_wall_collision(self, x, y, z):
    # Check if the proposed coordinates are within the bounds of the map
    if x < self.map.min_x or x > self.map.max_x or y < self.map.min_y or y > self.map.max_y or z < self.map.min_z or z > self.map.max_z:
      return True

    # Check if the proposed coordinates correspond to a wall tile
    for tile in self.map.tiles:
      if tile["x"] == x and tile["y"] == y and tile["z"] == z and tile["wall"]:
        return True

    # If no collisions were detected, return False
    return False

  # Method to check if a player's proposed movement will take them outside the boundaries of the map
  def check_boundary_collision(self, x, y, z):
    # Check if the proposed coordinates are within the bounds of the map
    if x < self.map.min_x or x > self.map.max_x or y < self.map.min_y or y > self.map.max_y or z < self.map.min_z or z > self.map.max_z:
      return True

    # If the proposed coordinates are within the bounds of the map, return False
    return False