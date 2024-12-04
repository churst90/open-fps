from include.event_handlers.user_movement import UserMovement
from include.managers.collision_manager import CollisionManager
from include.custom_logger import get_logger

class UserService:
    def __init__(self, user_registry, map_registry, event_dispatcher, logger=None):
        self.user_registry = user_registry
        self.map_registry = map_registry
        self.event_dispatcher = event_dispatcher
        self.logger = logger or get_logger('user_service', debug_mode=True)
        self.setup_event_subscriptions()

    def setup_event_subscriptions(self):
        """Subscribe to all necessary user-related events."""
        self.logger.debug("Setting up event subscriptions for UserService.")
        self.event_dispatcher.subscribe('user_account_create_request', self.create_account)
        self.event_dispatcher.subscribe('user_account_login_request', self.login)
        self.event_dispatcher.subscribe('user_account_logout_request', self.logout)
        self.event_dispatcher.subscribe('user_move_request', self.move_user)
        self.event_dispatcher.subscribe('user_turn_request', self.turn_user)

    async def create_account(self, event_data):
        """Handle account creation."""
        username = event_data['username']
        self.logger.info(f"Received account creation request for username: {username}")
        success = await self.user_registry.create_account(event_data)
        if success:
            self.logger.info(f"Account created successfully for username: {username}")
            await self.event_dispatcher.dispatch('user_account_create_ok', {'username': username})
        else:
            self.logger.warning(f"Failed to create account for username: {username}")
            await self.event_dispatcher.dispatch('user_account_create_fail', {'username': username})

    async def login(self, event_data):
        """Handle user login."""
        username = event_data['username']
        self.logger.info(f"Received login request for username: {username}")
        password = event_data['password']
        user_data = await self.user_registry.authenticate_user(username, password)
        if user_data:
            self.logger.info(f"Login successful for username: {username}")
            await self.event_dispatcher.dispatch('user_login_ok', {'username': username, 'user_data': user_data})
        else:
            self.logger.warning(f"Login failed for username: {username}")
            await self.event_dispatcher.dispatch('user_login_fail', {'username': username, 'message': 'Authentication failed'})

    async def logout(self, event_data):
        """Handle user logout."""
        username = event_data['username']
        self.logger.info(f"Received logout request for username: {username}")
        success = await self.user_registry.deauthenticate_user(username)
        if success:
            self.logger.info(f"Logout successful for username: {username}")
            await self.event_dispatcher.dispatch('user_logout_ok', {'username': username})
        else:
            self.logger.warning(f"Logout failed for username: {username}")
            await self.event_dispatcher.dispatch('user_logout_fail', {'username': username, 'message': 'Logout failed'})

    async def move_user(self, event_data):
        """Handle user movement."""
        username = event_data['username']
        direction = event_data['direction']
        distance = event_data['distance']
        self.logger.info(f"Received move request for username: {username}, direction: {direction}, distance: {distance}")
        success, position = await self.user_registry.move_user(username, direction, distance)
        if success:
            self.logger.info(f"Movement successful for username: {username}, new position: {position}")
            await self.event_dispatcher.dispatch('user_move_ok', {'username': username, 'position': position})
        else:
            self.logger.warning(f"Movement failed for username: {username}")
            await self.event_dispatcher.dispatch('user_move_fail', {'username': username})

    async def turn_user(self, event_data):
        """Handle user turning."""
        username = event_data['username']
        turn_direction = event_data['turn_direction']
        self.logger.info(f"Received turn request for username: {username}, turn direction: {turn_direction}")
        success, result = await self.user_registry.turn_user(username, turn_direction)
        if success:
            self.logger.info(f"Turn successful for username: {username}, new yaw: {result[0]}, pitch: {result[1]}")
            await self.event_dispatcher.dispatch('user_turn_ok', {'username': username, 'yaw': result[0], 'pitch': result[1]})
        else:
            self.logger.warning(f"Turn failed for username: {username}")
            await self.event_dispatcher.dispatch('user_turn_fail', {'username': username})
