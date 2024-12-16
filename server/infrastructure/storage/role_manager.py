# infrastructure/storage/role_manager.py
import json
from pathlib import Path
import logging

class RoleManager:
    _instance = None

    @classmethod
    def get_instance(cls, roles_file='roles.json', user_roles_file='user_roles.json'):
        if cls._instance is None:
            cls._instance = cls(roles_file, user_roles_file)
        return cls._instance

    def __init__(self, roles_file='roles.json', user_roles_file='user_roles.json', logger=None):
        if RoleManager._instance is not None:
            raise ValueError("Use get_instance() instead")
        self.logger = logger or logging.getLogger("RoleManager")
        self.roles_file = Path(roles_file)
        self.user_roles_file = Path(user_roles_file)

        self.roles = {}
        self.user_roles = {}

        self.load_roles()

    def load_roles(self):
        if self.roles_file.exists():
            with self.roles_file.open("r", encoding="utf-8") as f:
                self.roles = json.load(f)
            self.logger.info("Roles loaded successfully.")
        else:
            self.logger.warning("No roles file found. Starting with empty roles.")
            self.roles = {}

        if self.user_roles_file.exists():
            with self.user_roles_file.open("r", encoding="utf-8") as f:
                self.user_roles = json.load(f)
            self.logger.info("User roles loaded successfully.")
        else:
            self.logger.warning("No user_roles file found. Starting with empty user assignments.")
            self.user_roles = {}

    def save_user_roles(self):
        with self.user_roles_file.open("w", encoding="utf-8") as f:
            json.dump(self.user_roles, f, indent=4)
        self.logger.info("User roles saved successfully.")

    def assign_role_to_user(self, role_name, username):
        if role_name in self.roles:
            self.user_roles[username] = role_name
            self.save_user_roles()
            self.logger.info(f"Assigned role '{role_name}' to user '{username}'.")
        else:
            self.logger.warning(f"Role '{role_name}' does not exist.")

    def has_permission(self, username, permission):
        role_name = self.user_roles.get(username)
        if role_name and role_name in self.roles:
            return permission in self.roles[role_name].get("permissions", [])
        return False
