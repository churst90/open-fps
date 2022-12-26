# module for reading and writing game data to disk

import base64
from cryptography.fernet import Fernet

class Data:
    def __init__(self):
        pass

    # encrypt the dictionary and write it to disk
    def export(self, dictionary):
        # encrypt the dictionary
        encrypted_dict = encrypt(dictionary)

        # convert the dictionary to bytes
        bytes_dict = convert_to_bytes(encrypted_dict)

        # write the bytes to a file with the name of the dictionary
        with open(dictionary + '.dat', 'wb') as f:
            f.write(bytes_dict)

    # Load a dictionary from disk and decrypt it
    def load(self, dictionary):
        # read the file
        with open(dictionary + '.dat', 'rb') as f:
            bytes_dict = f.read()

        # decrypt the bytes
        decrypted_dict = decrypt(bytes_dict)

        # return the decrypted dictionary
        return decrypted_dict
