import pickle

class Data:
    def __init__(self, key):
        self.f = key

    def export(self, dictionary, filename):
        obj = pickle.dumps(dictionary, protocol=pickle.HIGHEST_PROTOCOL)
        print("file serialized")

        encrypted_obj = self.f.encrypt(obj)
        print("file encrypted.")

        with open(filename + '.dat', 'wb') as f:
            f.write(encrypted_obj)
            print("wrote file to disk")

    def load(self, dictionary):
        try:
            # read the file
            with open(dictionary + '.dat', 'rb') as f:
                bytes_dict = f.read()
                print("file loaded")
        except FileNotFoundError:
            print("file not found")
            return {}

        try:
            # decrypt the bytes
            decrypted_bytes = self.f.decrypt(bytes_dict)
            print("File decrypted")
        except:
            # If the token is invalid, return False
            print("Token is invalid")
            return False

        # convert the decrypted bytes to a pickle object
        decrypted_obj = pickle.loads(decrypted_bytes)

        # return the decrypted object
        return decrypted_obj
