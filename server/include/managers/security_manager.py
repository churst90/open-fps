import asyncio
import os
import subprocess
from cryptography.fernet import Fernet
from datetime import datetime, timedelta
from include.custom_logger import get_logger

class SecurityManager:
    _instance = None

    def __new__(cls, key_file_path='keys/security.key', logger=None):
        if cls._instance is None:
            cls._instance = super(SecurityManager, cls).__new__(cls)
            cls._instance.initialize(key_file_path, logger)
        return cls._instance

    @classmethod
    def get_instance(cls, key_file_path='keys/security.key', logger=None):
        return cls.__new__(cls, key_file_path, logger)

    def initialize(self, key_file_path, logger):
        # Set up logger
        self.logger = logger or get_logger('security_manager', debug_mode=True)

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
        """Generate a new encryption key and save it."""
        try:
            self.key = Fernet.generate_key()
            await self.save_key()
            self.logger.info("New encryption key generated and saved.")
        except Exception as e:
            self.logger.exception("Error generating encryption key.")

    async def save_key(self):
        """Save the encryption key to the specified file."""
        try:
            with open(self.key_file_path, "wb") as keyfile:
                keyfile.write(self.key)
            self.logger.debug(f"Encryption key saved to {self.key_file_path}.")
        except Exception as e:
            self.logger.exception("Error saving encryption key.")

    async def load_key(self):
        """Load the encryption key from file, or generate a new one if not found."""
        try:
            if os.path.exists(self.key_file_path):
                with open(self.key_file_path, "rb") as keyfile:
                    self.key = keyfile.read()
                self.last_rotation = datetime.fromtimestamp(os.path.getmtime(self.key_file_path))
                self.logger.info("Encryption key loaded successfully.")
            else:
                self.logger.warning("No encryption key found. Generating a new one...")
                await self.generate_key()
        except Exception as e:
            self.logger.exception("Error loading encryption key.")

    async def rotate_key(self, interval):
        """Periodically generate a new encryption key."""
        while True:
            try:
                now = datetime.now()
                if self.last_rotation is None or now - self.last_rotation >= timedelta(days=interval):
                    await self.generate_key()
                    self.last_rotation = now
                    self.logger.info("Encryption key rotated successfully.")
                await asyncio.sleep(24 * 60 * 60)  # Sleep for one day
            except asyncio.CancelledError:
                self.logger.debug("Key rotation task cancelled.")
                break
            except Exception as e:
                self.logger.exception("Error during key rotation.")

    async def start_key_rotation(self, interval):
        """Start the key rotation task."""
        try:
            self.rotation_task = asyncio.create_task(self.rotate_key(interval))
            self.logger.info(f"Key rotation task started with interval: {interval} days.")
        except Exception as e:
            self.logger.exception("Error starting key rotation task.")

    async def stop_key_rotation(self):
        """Stop the key rotation task."""
        try:
            if self.rotation_task and not self.rotation_task.done():
                self.rotation_task.cancel()
                await self.rotation_task
                self.logger.info("Key rotation task stopped.")
                self.rotation_task = None
        except Exception as e:
            self.logger.exception("Error stopping key rotation task.")

    async def ensure_ssl_certificate(self):
        """Generate SSL certificate and private key if they don't exist."""
        try:
            if not os.path.exists(self.cert_file) or not os.path.exists(self.key_file):
                self.logger.warning("No certificate or key found. Generating a self-signed certificate...")
                subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:4096', '-keyout', self.key_file, 
                                '-out', self.cert_file, '-days', '365', '-nodes', '-subj', '/CN=localhost'], check=True)
                self.logger.info("Certificate and key pair generated successfully.")
            else:
                self.logger.debug("Certificate and key already exist.")
        except subprocess.CalledProcessError as e:
            self.logger.error("Error generating SSL certificate.", exc_info=e)

    def get_key(self):
        """Return a Fernet instance initialized with the current encryption key."""
        if self.key:
            self.logger.debug("Returning Fernet encryption instance.")
            return Fernet(self.key)
        else:
            self.logger.warning("Encryption key is not loaded.")
            return None
