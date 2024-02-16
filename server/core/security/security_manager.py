import asyncio
import os
import time
from cryptography.fernet import Fernet
from datetime import datetime, timedelta

class SecurityManager:
    _instance = None

    def __new__(cls, key_file=None):
        if cls._instance is None:
            cls._instance = super(SecurityManager, cls).__new__(cls)
            cls._instance.key_file = key_file
            cls._instance.key = None
            cls._instance.last_rotation = None
            cls._instance.rotation_task = None  # Initialize the rotation task reference
        return cls._instance

    @staticmethod
    def get_instance(key_file=None):
        if SecurityManager._instance is None:
            SecurityManager(key_file)
        return SecurityManager._instance

    async def generate_key(self):
        self.key = Fernet.generate_key()

    async def save_key(self):
        with open(self.key_file, "wb") as f:
            f.write(self.key)

    async def load_key(self):
        if os.path.exists(self.key_file):
            with open(self.key_file, "rb") as f:
                self.key = f.read()
            self.last_rotation = datetime.fromtimestamp(os.path.getmtime(self.key_file))
        else:
            await self.generate_key()
            await self.save_key()
            self.last_rotation = datetime.now()

    async def rotate_key(self, interval):
        while True:
            now = datetime.now()
            if self.last_rotation is None or now - self.last_rotation >= timedelta(days=interval):
                await self.generate_key()
                await self.save_key()
                self.last_rotation = now
            await asyncio.sleep(24 * 60 * 60)  # Check once a day

    def start_key_rotation(self, interval):
        self.rotation_task = asyncio.create_task(self.rotate_key(interval))

    async def stop_key_rotation(self):
        if self.rotation_task and not self.rotation_task.done():
            self.rotation_task.cancel()
            try:
                # Wait for the task cancellation to complete, if necessary
                await self.rotation_task
            except asyncio.CancelledError:
                # Handle the cancellation error
                print("Key rotation task cancelled.")
            finally:
                self.rotation_task = None  # Reset the task reference

    def get_key(self):
        return Fernet(self.key)

# Example of using the SecurityManager in an async environment
async def main():
    key_file = 'path/to/key.file'

    # Example usage of the key for encryption/decryption
    fernet = manager.get_key()
    encrypted_data = fernet.encrypt(b"Secret data")
    decrypted_data = fernet.decrypt(encrypted_data)
    print(decrypted_data)

# Run the main function in an asyncio event loop
if __name__ == '__main__':
    asyncio.run(main())
