# infrastructure/security/security_manager.py
import jwt
import datetime
import logging
from typing import Optional
from infrastructure.storage.role_manager import RoleManager
from infrastructure.logging.custom_logger import get_logger

class SecurityManager:
    """
    SecurityManager handles authentication (JWT) and authorization (role-based checks).
    It integrates with RoleManager for checking permissions.
    """

    def __init__(
        self,
        role_manager: RoleManager,
        jwt_secret: str = "SUPER_SECRET_KEY",
        jwt_algorithm: str = "HS256",
        jwt_exp_delta_seconds: int = 3600,
        logger: Optional[logging.Logger] = None
    ):
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
        self.logger = logger or get_logger("SecurityManager", debug_mode=False)
        self.logger.debug(f"SecurityManager initialized with jwt_algorithm='{self.jwt_algorithm}', jwt_exp_delta={self.jwt_exp_delta_seconds}")

    def create_token(self, username: str) -> str:
        """
        Create a JWT token for the given username with configured expiration.
        """
        payload = {
            "username": username,
            "exp": datetime.datetime.utcnow() + datetime.timedelta(seconds=self.jwt_exp_delta_seconds)
        }
        token = jwt.encode(payload, self.jwt_secret, algorithm=self.jwt_algorithm)
        self.logger.debug(f"Created JWT token for user='{username}' with expiration in {self.jwt_exp_delta_seconds}s.")
        return token

    def check_jwt(self, token: str) -> Optional[str]:
        """
        Validate a JWT token and return the username if valid, else None.
        """
        try:
            payload = jwt.decode(token, self.jwt_secret, algorithms=[self.jwt_algorithm])
            username = payload.get("username")
            self.logger.debug(f"JWT validated successfully for user='{username}'.")
            return username
        except (jwt.ExpiredSignatureError, jwt.InvalidTokenError) as e:
            self.logger.debug(f"JWT validation failed: {e}")
            return None

    def is_authenticated(self, username: str, token: str) -> bool:
        """
        Check if the given token is valid and corresponds to the given username.
        """
        decoded_username = self.check_jwt(token)
        if decoded_username == username:
            self.logger.debug(f"User '{username}' authenticated successfully.")
            return True
        self.logger.debug(f"Authentication failed: token does not match username='{username}'.")
        return False

    def user_has_permission(self, username: str, token: str, permission: str) -> bool:
        """
        Check if the given user (validated by token) has the specified permission.
        """
        if not self.is_authenticated(username, token):
            self.logger.debug(f"user_has_permission: user='{username}' not authenticated.")
            return False

        role_name = self.role_manager.user_roles.get(username)
        if not role_name or role_name not in self.role_manager.roles:
            self.logger.debug(f"user_has_permission: user='{username}' has no valid role assigned.")
            return False

        role_permissions = self.role_manager.roles[role_name].get("permissions", [])
        has_perm = (permission in role_permissions)
        self.logger.debug(f"user_has_permission: user='{username}', role='{role_name}', perm='{permission}', result={has_perm}")
        return has_perm

    def user_has_any_permission(self, username: str, token: str, permissions: list) -> bool:
        """
        Check if user has at least one permission from the provided list.
        """
        if not self.is_authenticated(username, token):
            self.logger.debug(f"user_has_any_permission: user='{username}' not authenticated.")
            return False

        role_name = self.role_manager.user_roles.get(username)
        if not role_name or role_name not in self.role_manager.roles:
            self.logger.debug(f"user_has_any_permission: user='{username}' no valid role.")
            return False

        role_permissions = self.role_manager.roles[role_name].get("permissions", [])
        for perm in permissions:
            if perm in role_permissions:
                self.logger.debug(f"user_has_any_permission: user='{username}' has permission '{perm}'.")
                return True

        self.logger.debug(f"user_has_any_permission: user='{username}' lacks any of the requested permissions.")
        return False

    def user_has_all_permissions(self, username: str, token: str, permissions: list) -> bool:
        """
        Check if user has all listed permissions.
        """
        if not self.is_authenticated(username, token):
            self.logger.debug(f"user_has_all_permissions: user='{username}' not authenticated.")
            return False

        role_name = self.role_manager.user_roles.get(username)
        if not role_name or role_name not in self.role_manager.roles:
            self.logger.debug(f"user_has_all_permissions: user='{username}' no valid role.")
            return False

        role_permissions = self.role_manager.roles[role_name].get("permissions", [])
        for perm in permissions:
            if perm not in role_permissions:
                self.logger.debug(f"user_has_all_permissions: user='{username}' missing perm='{perm}'.")
                return False

        self.logger.debug(f"user_has_all_permissions: user='{username}' has all requested permissions.")
        return True
