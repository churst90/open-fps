import json
from pathlib import Path

class RoleManager:
    _instance = None

    @classmethod
    def get_instance(cls):
        if cls._instance is None:
            cls._instance = cls()
            cls._instance.initialize_roles()
            cls._instance.load_roles()  # Load roles on instantiation
        return cls._instance

    def __init__(self):
        if RoleManager._instance is not None:
            raise ValueError("Access the instance through `get_instance()` method.")
        self.roles = {}
        self.user_roles = {}  # Tracks roles by user for efficient lookup
        self.roles_file = Path("roles.json")  # Define the file path for roles persistence

    def initialize_roles(self):
        self.roles = {
            'developer': {"permissions": ["add_tile", "remove_tile", "add_zone", "remove_zone", "create_map", "remove_map"]},
            'admin': {"permissions": []},
            'player': {"permissions": []}
        }
        # If roles file exists, load roles from file instead of initializing
        if self.roles_file.exists():
            self.load_roles()

    def save_roles(self):
        """Save the user roles dictionary to a file."""
        with self.roles_file.open("w") as file:
            json.dump(self.user_roles, file, indent=4)
        print("User roles saved successfully.")

    def load_roles(self):
        """Load the user roles dictionary from a file."""
        if self.roles_file.exists():
            with self.roles_file.open("r") as file:
                self.user_roles = json.load(file)
            print("User roles loaded successfully.")
        else:
            print("No roles file found. Starting with an empty roles assignment.")

    def assign_role_to_user(self, role_name, username):
        if role_name in self.roles:
            self.user_roles[username] = role_name  # Assuming each user can only have one role
            self.save_roles()  # Save roles after assignment

    def remove_role_from_user(self, username):
        if username in self.user_roles:
            del self.user_roles[username]
            self.save_roles()  # Save roles after removal

    def has_permission(self, username, permission):
        print("has permission method called in role manager")
        role_name = self.user_roles.get(username)
        if role_name:
            role = self.roles.get(role_name)
            if role and permission in role.get("permissions", []):
                return True
        return False
