import asyncio
import os
import subprocess
from cryptography.fernet import Fernet
from datetime import datetime, timedelta

class SecurityManager:
    _instance = None

    def __new__(cls, key_file_path='keys/security.key'):
        if cls._instance is None:
            cls._instance = super(SecurityManager, cls).__new__(cls)
            cls._instance.initialize(key_file_path)
        return cls._instance

    @classmethod
    def get_instance(cls, key_file_path='security.key'):
        return cls.__new__(cls, key_file_path)

    def initialize(self, key_file_path):
        # Set up file paths for the encryption key, certificate, and private key
        base_dir = os.path.join(os.path.dirname(__file__), '../keys')
        self.key_file_path = os.path.join(base_dir, key_file_path)
        self.cert_file = os.path.join(base_dir, 'cert.pem')
        self.key_file = os.path.join(base_dir, 'key.pem')

        # Initialize encryption key and rotation task variables
        self.key = None
        self.last_rotation = None
        self.rotation_task = None

    async def generate_key(self):
        # Generate a new encryption key and save it
        self.key = Fernet.generate_key()
        await self.save_key()

    async def save_key(self):
        # Save the encryption key to the specified file
        with open(self.key_file_path, "wb") as keyfile:
            keyfile.write(self.key)

    async def load_key(self):
        # Load the encryption key from file, or generate a new one if not found
        if os.path.exists(self.key_file_path):
            with open(self.key_file_path, "rb") as keyfile:
                self.key = keyfile.read()
            self.last_rotation = datetime.fromtimestamp(os.path.getmtime(self.key_file_path))
        else:
            print("No encryption key found. Generating a new one...")
            await self.generate_key()

    async def rotate_key(self, interval):
        # Periodically generate a new encryption key
        while True:
            try:
                now = datetime.now()
                if self.last_rotation is None or now - self.last_rotation >= timedelta(days=interval):
                    await self.generate_key()
                    self.last_rotation = now
                await asyncio.sleep(24 * 60 * 60)  # Sleep for one day
            except asyncio.CancelledError:
                break

    async def start_key_rotation(self, interval):
        # Start the key rotation task
        self.rotation_task = asyncio.create_task(self.rotate_key(interval))

    async def stop_key_rotation(self):
        # Stop the key rotation task
        if self.rotation_task and not self.rotation_task.done():
            self.rotation_task.cancel()
            await self.rotation_task
            print("Key rotation task cancelled.")
            self.rotation_task = None

    async def ensure_ssl_certificate(self):
        # Generate SSL certificate and private key if they don't exist
        if not os.path.exists(self.cert_file) or not os.path.exists(self.key_file):
            print("Neither a certificate nor a key were found. Generating a self-signed certificate now...")
            subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:4096', '-keyout', self.key_file, '-out', self.cert_file, '-days', '365', '-nodes', '-subj', '/CN=localhost'], check=True)
            print("Certificate and key pair generated successfully.")

    def get_key(self):
        # Return a Fernet instance initialized with the current encryption key
        return Fernet(self.key) if self.key else None
