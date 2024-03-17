class RoleManager:
    _instance = None

    @classmethod
    def get_instance(cls):
        if cls._instance is None:
            cls._instance = cls()
            cls._instance.initialize_roles()
        return cls._instance

    def __init__(self):
        if RoleManager._instance is not None:
            raise ValueError("Access the instance through `get_instance()` method.")
        self.roles = {}
        self.user_roles = {}  # Tracks roles by user for efficient lookup

    def initialize_roles(self):
        self.roles = {
            'developer': {"permissions": ["add_tile", "remove_tile", "add_zone", "remove_zone", "create_map", "remove_map"]},
            'admin': {"permissions": []},
            'player': {"permissions": []}
        }
        # Initialize user_roles as empty; will be filled as users are assigned roles
        self.user_roles = {}

    def assign_role_to_user(self, role_name, username):
        if role_name in self.roles:
            self.user_roles[username] = role_name  # Assuming each user can only have one role

    def remove_role_from_user(self, username):
        if username in self.user_roles:
            del self.user_roles[username]

    def has_permission(self, username, permission):
        role_name = self.user_roles.get(username)
        if role_name and permission in self.roles.get(role_name, {}).get("permissions", []):
            return True
        return False
