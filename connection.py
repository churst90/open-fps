import socket

class Connection:
  def __init__(self, host, port):
    self.host = host
    self.port = port
    self.server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)

  def connect(self):
    self.server_socket.connect((self.host, self.port))
    
  def disconnect(self):
    self.server_socket.close()