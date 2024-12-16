# infrastructure/security/security_manager.py
import jwt
import datetime
import logging
from typing import Optional
from infrastructure.storage.role_manager import RoleManager

class SecurityManager:
    """
    The SecurityManager handles authentication (JWT) and authorization (role-based checks).
    """

    def __init__(self, role_manager: RoleManager, jwt_secret: str = "SUPER_SECRET_KEY", jwt_algorithm: str = "HS256", jwt_exp_delta_seconds: int = 3600, logger: Optional[logging.Logger] = None):
        """
        :param role_manager: Instance of RoleManager for role-based authorization.
        :param jwt_secret: Secret key for JWT encoding/decoding.
        :param jwt_algorithm: JWT signing algorithm.
        :param jwt_exp_delta_seconds: JWT expiration time in seconds.
        :param logger: Optional logger.
        """
        self.role_manager = role_manager
        self.jwt_secret = jwt_secret
        self.jwt_algorithm = jwt_algorithm
        self.jwt_exp_delta_seconds = jwt_exp_delta_seconds
        self.logger = logger or logging.getLogger("SecurityManager")

    def create_token(self, username: str) -> str:
        """
        Create a JWT token for the given username with configured expiration.
        """
        payload = {
            "username": username,
            "exp": datetime.datetime.utcnow() + datetime.timedelta(seconds=self.jwt_exp_delta_seconds)
        }
        token = jwt.encode(payload, self.jwt_secret, algorithm=self.jwt_algorithm)
        self.logger.debug(f"Token created for user '{username}'.")
        return token

    def check_jwt(self, token: str) -> Optional[str]:
        """
        Validate a JWT token and return the username if valid, else None.
        """
        try:
            payload = jwt.decode(token, self.jwt_secret, algorithms=[self.jwt_algorithm])
            return payload.get("username")
        except (jwt.ExpiredSignatureError, jwt.InvalidTokenError) as e:
            self.logger.debug(f"Token validation failed: {e}")
            return None

    def is_authenticated(self, username: str, token: str) -> bool:
        """
        Check if the given token is valid and corresponds to the given username.
        """
        decoded_username = self.check_jwt(token)
        if decoded_username == username:
            self.logger.debug(f"User '{username}' authenticated successfully.")
            return True
        self.logger.debug(f"User '{username}' not authenticated.")
        return False

    def user_has_permission(self, username: str, token: str, permission: str) -> bool:
        if not self.is_authenticated(username, token):
            return False

        role_name = self.role_manager.user_roles.get(username)
        if not role_name or role_name not in self.role_manager.roles:
            return False

        role_permissions = self.role_manager.roles[role_name].get("permissions", [])
        return permission in role_permissions

    def user_has_any_permission(self, username: str, token: str, permissions: list) -> bool:
        if not self.is_authenticated(username, token):
            return False

        role_name = self.role_manager.user_roles.get(username)
        if not role_name or role_name not in self.role_manager.roles:
            return False

        role_permissions = self.role_manager.roles[role_name].get("permissions", [])
        return any(perm in role_permissions for perm in permissions)

    def user_has_all_permissions(self, username: str, token: str, permissions: list) -> bool:
        if not self.is_authenticated(username, token):
            return False

        role_name = self.role_manager.user_roles.get(username)
        if not role_name or role_name not in self.role_manager.roles:
            return False

        role_permissions = self.role_manager.roles[role_name].get("permissions", [])
        return all(perm in role_permissions for perm in permissions)
