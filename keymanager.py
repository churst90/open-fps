import os
import time
from cryptography.fernet import Fernet

class KeyManager:
    def __init__(self, key_file):
        self.key_file = key_file
        self.key = None

    def generate_key(self):
        # Generate a new key
        self.key = Fernet.generate_key()

    def save_key(self):
        # Save the key to a file on disk
        with open(self.key_file, "wb") as f:
            f.write(self.key)

    def load_key(self):
        # Load the key from the file on disk
        with open(self.key_file, "rb") as f:
            self.key = f.read()

    def rotate_key(self, interval):
        # Rotate the key every `interval` days
        while True:
            # Generate a new key
            self.generate_key()

            # Save the key to a file on disk
            self.save_key()

            # Wait for the specified interval before rotating the key again
            time.sleep(interval * 24 * 60 * 60)
