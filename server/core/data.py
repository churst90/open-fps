# standard imports
import json

# project specific imports
from .modules.player import Player
from .modules.map_manager import Map, MapRegistry

class Data:
    def __init__(self, key):
        self.f = key

    def export(self, obj, filename):
        # Recursive conversion of objects to dictionaries
        def convert_to_dict(o):
            if hasattr(o, "to_dict"):
                return o.to_dict()  # Convert Player instances to dictionaries
            elif isinstance(o, dict):
                return {k: convert_to_dict(v) for k, v in o.items()}  # Recursive call for nested dictionaries
            elif isinstance(o, list):
                return [convert_to_dict(item) for item in o]  # Recursive call for lists
            else:
                return o  # Return the object as is if no conversion is possible

        obj = convert_to_dict(obj)  # Convert the top-level object
        
        # Proceed with serialization as before
        json_str = json.dumps(obj)
        print("Data serialized to JSON format.")

        encrypted_json = self.f.encrypt(json_str.encode())
        print("Data encrypted.")

        with open(filename + '.dat', 'wb') as f:
            f.write(encrypted_json)
            print(f"Encrypted data written to {filename}.dat.")

    def load(self, filename):
        try:
            # Read the encrypted data from the file
            with open(filename + '.dat', 'rb') as f:
                encrypted_data = f.read()
                print(f"Encrypted file {filename}.dat loaded.")
        except FileNotFoundError:
            print(f"File {filename}.dat not found.")
            return {}

        try:
            # Decrypt the data
            decrypted_data = self.f.decrypt(encrypted_data)
            print("Data decrypted.")
        except Exception as e:
            print(f"Decryption failed: {e}")
            return {}

        # Deserialize the JSON formatted string to a dictionary
        dictionary = json.loads(decrypted_data.decode())

        # Example for converting to Map and Player objects
        if filename == "maps.dat":
            return {map_name: Map.from_dict(map_data) for map_name, map_data in dictionary.items()}
        elif filename == "users.dat":
            return {username: Player.from_dict(user_data) for username, user_data in dictionary.items()}
        else:
            return dictionary
