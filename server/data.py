import json

class Data:
    def __init__(self, key):
        self.f = key

    def export(self, dictionary, filename):
        # Serialize the dictionary to a JSON formatted string
        json_str = json.dumps(dictionary)
        print("Data serialized to JSON format.")

        # Encrypt the JSON string
        encrypted_json = self.f.encrypt(json_str.encode())
        print("Data encrypted.")

        # Write the encrypted data to a file
        with open(filename + '.dat', 'wb') as f:
            f.write(encrypted_json)
            print("Encrypted data written to disk.")

    def load(self, filename):
        try:
            # Read the encrypted data from the file
            with open(filename + '.dat', 'rb') as f:
                encrypted_data = f.read()
                print("Encrypted file loaded.")
        except FileNotFoundError:
            print("File not found.")
            return {}

        try:
            # Decrypt the data
            decrypted_data = self.f.decrypt(encrypted_data)
            print("Data decrypted.")
        except Exception as e:
            print("Decryption failed:", e)
            return {}

        # Deserialize the JSON formatted string to a dictionary
        dictionary = json.loads(decrypted_data.decode())

        return dictionary