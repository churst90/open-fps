import asyncio
import bcrypt


class User:
    def __init__(self):
        self._username = ""
        self._password = ""
        self._current_map = ""
        self._current_zone = ""
        self._logged_in = False
        self._position = ()
        self._yaw = 0
        self._pitch = 0
        self._health = 10000
        self._energy = 10000
        self.inventory = {}

    @property
    def username(self):
        return self._username

    @username.setter
    def username(self, value):
        if isinstance(value, str):
            self._username = value
        else:
            raise ValueError("Username must be a string.")

    @property
    def password(self):
        # Return the hashed password
        return self._password

    async def set_password(self, raw_password):
        """Hashes the password and sets it."""
        if isinstance(raw_password, str):
            hashed_password = await asyncio.to_thread(
                bcrypt.hashpw, raw_password.encode("utf-8"), bcrypt.gensalt()
            )
            self._password = hashed_password.decode("utf-8")
        else:
            raise ValueError("Password must be a string.")

    def check_password(self, raw_password):
        """Verifies the password against the hashed value."""
        if self._password:
            return bcrypt.checkpw(raw_password.encode("utf-8"), self._password.encode("utf-8"))
        return False

    @property
    def position(self):
        return self._position

    @position.setter
    def position(self, value):
        if isinstance(value, tuple):
            self._position = value
        else:
            raise ValueError("Position must be a tuple.")

    @property
    def current_map(self):
        return self._current_map

    @current_map.setter
    def current_map(self, value):
        if isinstance(value, str):
            self._current_map = value
        else:
            raise ValueError("Current map must be a string.")

    @property
    def current_zone(self):
        return self._current_zone

    @current_zone.setter
    def current_zone(self, value):
        if isinstance(value, str):
            self._current_zone = value
        else:
            raise ValueError("Current zone must be a string.")

    @property
    def logged_in(self):
        return self._logged_in

    @logged_in.setter
    def logged_in(self, value):
        if isinstance(value, bool):
            self._logged_in = value
        else:
            raise ValueError("Logged-in status must be a boolean.")

    @property
    def yaw(self):
        return self._yaw

    @yaw.setter
    def yaw(self, value):
        if isinstance(value, (int, float)):
            self._yaw = value
        else:
            raise ValueError("Yaw must be a number.")

    @property
    def pitch(self):
        return self._pitch

    @pitch.setter
    def pitch(self, value):
        if isinstance(value, (int, float)):
            self._pitch = value
        else:
            raise ValueError("Pitch must be a number.")

    @property
    def health(self):
        return self._health

    @health.setter
    def health(self, value):
        if isinstance(value, int) and 0 <= value <= 10000:
            self._health = value
        else:
            raise ValueError("Health must be an integer between 0 and 10000.")

    @property
    def energy(self):
        return self._energy

    @energy.setter
    def energy(self, value):
        if isinstance(value, int) and 0 <= value <= 10000:
            self._energy = value
        else:
            raise ValueError("Energy must be an integer between 0 and 10000.")

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
        except Exception as e:
            print(f"Couldn't convert the user dictionary to a class instance: {e}")
            return False

    @classmethod
    def from_dict(cls, data):
        try:
            user_instance = cls()
            user_instance.username = data["username"]
            user_instance._password = data["password"]  # Direct assignment, as password is hashed
            user_instance.current_map = data["current_map"]
            user_instance.logged_in = data["logged_in"]
            user_instance.health = data["health"]
            user_instance.energy = data["energy"]
            user_instance.inventory = data["inventory"]
            user_instance.yaw = data["yaw"]
            user_instance.pitch = data["pitch"]
            return user_instance
        except Exception as e:
            print(f"Couldn't convert the user class instance to a dictionary: {e}")
            return False
