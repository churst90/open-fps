class Chat:
  def __init__(self):
    self.global_messages = []
    self.map_messages = {}
    self.private_messages = {}

  def send_global_message(self, sender, message):
    # Add the global message to the list of global messages
    self.global_messages.append({"sender": sender, "message": message})

  def send_map_message(self, map_name, sender, message):
    # Check if the map name exists in the dictionary of map messages
    if map_name not in self.map_messages:
      self.map_messages[map_name] = []

    # Add the map message to the list of messages for the specified map
    self.map_messages[map_name].append({"sender": sender, "message": message})

  def send_private_message(self, sender, recipient, message):
    # Check if the recipient exists in the dictionary of private messages
    if recipient not in self.private_messages:
      self.private_messages[recipient] = []

    # Add the private message to the list of private messages for the recipient
    self.private_messages[recipient].append({"sender": sender, "message": message})

  def get_global_messages(self):
    return self.global_messages

  def get_map_messages(self, map_name):
    if map_name in self.map_messages:
      return self.map_messages[map_name]
    else:
      return []

  def get_private_messages(self, recipient):
    if recipient in self.private_messages:
      return self.private_messages[recipient]
    else:
      return []
