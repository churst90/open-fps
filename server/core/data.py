# data.py
import json
from cryptography.fernet import Fernet
from core.security.security_manager import SecurityManager as sm

class Data:
    def __init__(self):
        self.sm = sm("security.key")
        self.sm.load_key()
        self.f = self.sm.get_key()

    def export(self, data_dict, filename):
        """
        Encrypts and saves the provided dictionary to disk.
        
        :param data_dict: Dictionary to be saved.
        :param filename: Filename for the saved file.
        """
        # Convert dictionary to JSON and encode to bytes
        json_data = json.dumps(data_dict).encode('utf-8')
        # Encrypt data
        encrypted_data = self.f.encrypt(json_data)
        # Write encrypted data to disk
        with open(filename, 'wb') as file:
            file.write(encrypted_data)
        print(f"Data successfully encrypted and written to {filename}.")

    def load(self, filename):
        """
        Loads and decrypts a dictionary from a file.
        
        :param filename: Filename to load and decrypt.
        :return: Decrypted dictionary or None if file does not exist.
        """
        try:
            # Read encrypted data from disk
            with open(filename, 'rb') as file:
                encrypted_data = file.read()
            # Decrypt data
            decrypted_data = self.f.decrypt(encrypted_data)
            # Convert JSON bytes back to a dictionary
            data_dict = json.loads(decrypted_data.decode('utf-8'))
            return data_dict
        except FileNotFoundError:
            print(f"File {filename} not found.")
            return None
        except Exception as e:
            print(f"An error occurred during file loading or decryption: {e}")
            return None
