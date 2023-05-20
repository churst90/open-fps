import threading
import time
from cryptography.fernet import Fernet

class SecurityManager:
    def __init__(self, key_file):
        self.key_file = key_file
        self.key = None
        self.key_rotation_thread = None

    def generate_key(self):
        # Generate a new key
        self.key = Fernet.generate_key()

    def save_key(self):
        # Save the key to a file on disk
        with open(self.key_file, "wb") as f:
            f.write(self.key)

    def load_key(self):
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

    def start_key_rotation(self, interval):
        # Start the key rotation thread
        self.key_rotation_thread = threading.Thread(target=self.rotate_key, args=(interval,))
        self.key_rotation_thread.start()

    def stopKeyRotation(self):
        # Stop the key rotation thread
        if self.key_rotation_thread is not None:
            self.key_rotation_thread.join()

    def get_key(self):
        return Fernet(self.key)