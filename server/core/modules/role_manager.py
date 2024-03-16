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
        self.user_roles = {}  # Tracks users by their roles

    def initialize_roles(self):
        # Initialize roles with empty lists for permissions and users
        self.roles = {
            'developer': {"permissions": ["manage_users", "change_map", "immune_to_health_changes", "all"], "users": set()},
            'admin': {"permissions": ["manage_users", "change_map"], "users": set()},
            'player': {"permissions": [], "users": set()}
        }

    def assign_role_to_user(self, role_name, username):
        if role_name in self.roles:
            self.roles[role_name]["users"].add(username)
            # Optionally, track roles per user if needed
            if username not in self.user_roles:
                self.user_roles[username] = set()
            self.user_roles[username].add(role_name)
        else:
            print(f"Role {role_name} does not exist.")

    def remove_role_from_user(self, role_name, username):
        if role_name in self.roles and username in self.roles[role_name]["users"]:
            self.roles[role_name]["users"].remove(username)
            if username in self.user_roles:
                self.user_roles[username].discard(role_name)

    def get_users_by_role(self, role_name):
        if role_name in self.roles:
            return self.roles[role_name]["users"]
        return set()  # Return an empty set if the role does not exist

    def get_permissions(self, role_name):
        if role_name in self.roles:
            return self.roles[role_name]["permissions"]
        return []
