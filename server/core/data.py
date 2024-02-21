# data.py
import json
from cryptography.fernet import Fernet
from core.security.security_manager import SecurityManager as sm

class Data:
    def __init__(self):
        self.f = None

    async def async_init(self):
        self.sm = sm.get_instance("security.key")
        await self.sm.load_key()  # Ensure the key is loaded asynchronously
        self.f = self.sm.get_key()

    async def export(self, data_dict, filename):
        try:
            # Convert dictionary to JSON and encode to bytes
            json_data = json.dumps(data_dict).encode('utf-8')
            # Encrypt data
            encrypted_data = self.f.encrypt(json_data)
            # Write encrypted data to disk
            with open(f"{filename}.dat", 'wb') as file:
                file.write(encrypted_data)
            print(f"Data successfully encrypted and written to {filename}.dat.")
        except Exception as e:
            print(f"Error saving data to {filename}.dat: {e}")

    async def load(self, filename):
        try:
            # Read encrypted data from disk
            with open(f"{filename}.dat", 'rb') as file:
                encrypted_data = file.read()
            # Decrypt data
            decrypted_data = self.f.decrypt(encrypted_data)
            # Convert JSON bytes back to a dictionary
            return json.loads(decrypted_data.decode('utf-8'))
            print("JSON converted to dictionary and returned .....")
        except FileNotFoundError:
            print(f"File {filename}.dat not found.")
            return None
        except Exception as e:
            print(f"An error occurred during file loading or decryption: {e}")
            return None