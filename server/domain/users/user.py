# domain/users/user.py
import asyncio
import bcrypt

class User:
    def __init__(self, username, password, current_map="Main", current_zone="Main", position=(0,0,0), current_energy=10000, current_health=10000, yaw=0, pitch=0):
        self.username = username
        self._password = password
        self.current_map = current_map
        self.current_zone = current_zone
        self.position = position
        self.yaw = yaw
        self.pitch = pitch
        self.health = current_health
        self.energy = current_energy
        self.inventory = {}
        self.logged_in = False
        self.role = "player"
        self.velocity = (0.0, 0.0, 0.0)

    async def set_password(self, raw_password: str):
        if not raw_password:
            raise ValueError("Password cannot be empty.")
        hashed_password = await asyncio.to_thread(
            bcrypt.hashpw, raw_password.encode('utf-8'), bcrypt.gensalt()
        )
        self._password = hashed_password.decode('utf-8')

    def check_password(self, raw_password: str) -> bool:
        if self._password:
            return bcrypt.checkpw(raw_password.encode('utf-8'), self._password.encode('utf-8'))
        return False

    def to_dict(self) -> dict:
        return {
            "username": self.username,
            "password": self._password,
            "current_map": self.current_map,
            "current_zone": self.current_zone,
            "position": self.position,
            "yaw": self.yaw,
            "pitch": self.pitch,
            "health": self.health,
            "energy": self.energy,
            "inventory": self.inventory,
            "logged_in": self.logged_in,
            "role": self.role,
            "velocity": self.velocity
        }

    @classmethod
    def from_dict(cls, data: dict):
        user = cls(
            username=data["username"],
            password="",
            current_map=data.get("current_map", "Main"),
            current_zone=data.get("current_zone", "Main"),
            position=tuple(data.get("position", (0,0,0))),
            current_energy=data.get("energy", 10000),
            current_health=data.get("health", 10000),
            yaw=data.get("yaw", 0),
            pitch=data.get("pitch", 0)
        )
        user._password = data["password"]
        user.inventory = data.get("inventory", {})
        user.logged_in = data.get("logged_in", False)
        user.role = data.get("role", "player")
        user.velocity = tuple(data.get("velocity", (0.0,0.0,0.0)))
        return user
