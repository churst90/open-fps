import asyncio
import bcrypt

# User class for creating individual user objects
class User:
    def __init__(self):
        # User instance features and atributes
        self.username = ""
        self.password = ""
        self.current_map = ""
        self.current_zone = ""
        self.logged_in = False
        self.position = ()
        self.yaw = 0
        self.pitch = 0
        self.health = 10000
        self.energy = 10000
        self.inventory = {}

    def get_username(self):
        try:
            return self.username
        except Exception as E:
            print(f"could not retrieve the user's username: {E}")
            return False

    def get_password(self):
        try:
            return self.password
        except Exception as E:
            print(f"Couldn't retrieve the user's password: {E}")
            return False

    def get_position(self):
        try:
            return self.position
        except Exception as E:
            print(f"Couldn't retrieve the user's position: {E}")
            return False

    def get_current_map(self):
        try:
            return self.current_map
        except Exception as E:
            print("Couldn't retrieve the users's current map: {E}")
            return False

    def get_pitch(self):
        try:
            return self.pitch
        except Exception as E:
            print(f"Couldn't retrieve the user's pitch: {E}")
            return False

    def get_yaw(self):
        try:
            return self.yaw
        except Exception as E:
            print(f"Couldn't retrieve the user's yaw: {E}")
            return False

    def get_current_zone(self):
        try:
            return self.current_zone
        except Exception as E:
            print(f"Couldn't retrieve the user's current zone: {E}")
            return False

    def get_health(self):
        try:
            return self.health
        except Exception as E:
            print(f"Couldn't retrieve the user's health: {E}")
            return False

    def get_energy(self):
        try:
            return self.energy
        except Exception as E:
            print(f"Couldn't retrieve the user's energy: {E}")
            return False

    def set_login_status(self, status):
        try:
            self.logged_in = status
            return
        except Exception as E:
            print(f"Couldn't set the user's status: {E}")
            return False

    def set_position(self, position):
        try:
            self.position = position
            return
        except Exception as E:
            print(f"Couldn't set the user's position: {E}")
            return False

    def set_health(self, health):
        try:
            self.health = health
        except Exception as E:
            print(f"Couldn't set the user's health: {E}")
            return False

    def set_energy(self, energy):
        try:
            self.energy = energy
            return
        except Exception as E:
            print(f"Couldn't set the user's energy: {E}")
            return False

    def set_pitch(self, pitch):
        try:
            self.pitch = pitch
            return
        except Exception as E:
            print(f"Couldn't set the user's pitch: {E}")
            return False

    def set_yaw(self, yaw):
        try:
            self.yaw = yaw
        except Exception as E:
            print(f"Couldn't set the user's yaw: {E}")
            return False

    def set_username(self, username):
        try:
            self.username = username
            return
        except Exception as E:
            print(f"Couldn't set the user's username: {E}")
            return False

    async def set_password(self, password):
        try:
            hashed_password = await asyncio.to_thread(bcrypt.hashpw, password.encode('utf-8'), bcrypt.gensalt())
            # Store the hashed password rather than the plain one
            self.password = hashed_password.decode('utf-8')
            return
        except Exception as E:
            print(f"Couldn't set the user's password: {E}")
            return False

    def set_current_map(self, map):
        try:
            self.current_map = map
            return
        except Exception as E:
            print(f"Couldn't set the user's current map: {E}")
            return False

    def to_dict(self):
        try:
            return {
                "username": self.username,
                "password": self.password,
                "logged_in": self.logged_in,
                "position": self.position,
                "current_map": self.current_map,
                "yaw": self.yaw,
                "pitch": self.pitch,
                "health": self.health,
                "energy": self.energy,
                "inventory": self.inventory,
            }
        except Exception as E:
            print(f"Couldn't convert the user dictionary to a class instance: {E}")
            return False

    @classmethod
    def from_dict(cls, data, event_dispatcher):
        try:
            user_instance = cls()
            user_instance.username = data['username']
            user_instance.password = data['password']
            user_instance.current_map =  data['current_map']
            user_instance.logged_in = data['logged_in']
            user_instance.health = data['health']
            user_instance.energy = data['energy']
            user_instance.inventory = data['inventory']
            user_instance.yaw = data['yaw']
            user_instance.pitch = data['pitch']
            return user_instance
        except Exception as E:
            print(f"Couldn't convert the user class instance to a dictionary: {E}")
            return False