import math
import json

# project specific imports
from include.event_dispatcher import EventDispatcher
from include.event_handlers.user_movement import UserMovement

class UserHandler:
    def __init__(self, event_dispatcher, user_service, role_manager):
        self.user_service = user_service
        self.event_dispatcher = event_dispatcher
        self.setup_subscriptions()
        self.role_manager= role_manager

    def setup_subscriptions(self):
        self.event_dispatcher.subscribe_internal('user_move_request', self.handle_user_move)
        self.event_dispatcher.subscribe_internal('user_turn_request', self.handle_user_turn)
        self.event_dispatcher.subscribe_internal('user_account_create_request', self.handle_create_account)
        self.event_dispatcher.subscribe_internal('user_account_login_request', self.handle_login)
        self.event_dispatcher.subscribe_internal('user_account_logout_request', self.handle_logout)

    async def handle_login(self, event_data):
        username = event_data['username']
        password = event_data['password']
        user_data = await self.user_service.login(username, password)
        if user_data:
            await self.event_dispatcher.dispatch('user_login_ok', {
                "message_type": "user_login_ok",
                'username': username,
                "user_data": user_data
            }, scope="private", user_id=username)
        else:
            await self.event_dispatcher.dispatch('user_login_fail', {
                "message_type": "user_login_fail",
                'username': username,
                'message': "Authentication failed"
            }, scope="private", user_id=username)

    async def handle_logout(self, event_data):
        # Extract the username from the event data
        username = event_data.get('username')
        map_name = event_data.get('map_name')

        # Check if the username is provided
        if not username:
            return

        # Attempt to log the user out via the UserService
        success = await self.user_service.logout(username, map_name)
        if success:
            # If logout is successful, dispatch a success event
            await self.event_dispatcher.dispatch("user_logout_ok", {
                "message_type": "user_logout_ok",
                "username": username,
            }, scope="private", user_id=username)

        else:
            # If logout fails, dispatch a failure event
            await self.event_dispatcher.dispatch("user_logout_fail", {
                "message_type": "user_logout_fail",
                "username": username,
                "messahge": "Logout failed. User might not be logged in."
            }, scope="private", user_id = username)

    async def handle_user_move(self, event_data):
        username = event_data['username']
        map_name = event_data['map_name']
        direction = event_data['direction']
        distance = event_data['distance']

        success, result = await self.user_service.move_user(username, direction, distance)

        if success:
            # Dispatch success event with new position
            await self.event_dispatcher.dispatch("user_move_ok", {
                "message_type": "user_move_ok",
                "username": username,
                "position": result  # new position
            }, scope = "map", map_id = map_name)
        else:
            # Dispatch failure event, possibly with error message
            await self.event_dispatcher.dispatch("user_move_failed", {
        "message_type": "user_move_fail",
                "username": username
            }, scope = "private", user_id = username)

    async def handle_user_turn(self, event_data):
        username = event_data.get('username')
        turn_direction = event_data.get('turn_direction')

        success, result = await self.user_service.turn_user(username, turn_direction)

        if success:
            # Dispatch event to update client with new orientation
            await self.event_dispatcher.dispatch("user_turn_ok", {
                "message_type": "user_turn_ok",
                "username": username,
                "yaw": result[0],
                "pitch": result[1]
            })
        else:
            # Dispatch failure event, possibly with error message
            await self.event_dispatcher.dispatch("user_turn_fail", {
                "message_type": "user_turn_fail",
                "username": username
            }, scope = "private", user_id = username)

    async def handle_create_account(self, event_data):
        # Extract data from the event
        username = event_data['username']

        try:
            # Call the UserService to create the account
            success = await self.user_service.create_account(event_data, self.role_manager)

            # If the account is successfully created, dispatch a success event
            if success:
                await self.event_dispatcher.dispatch('user_account_create_ok', {
        "message_type": "user_account_create_ok",
                    'username': username,
                    'message': 'Account successfully created.'
                }, scope = "private", user_id = username)
            else:
                await self.event_dispatcher.dispatch('user_account_create_fail', {
        "message_type": "user_account_create_fail",
                    'username': username,
                    'message': "Couldn't create account. Username may already exist."
                }, scope = "private", user_id = username)

        except Exception as e:
            # Log the exception
            print(f"Error creating account for {username}: {str(e)}")

            # Dispatch a failure event
            await self.event_dispatcher.dispatch('user_account_create_fail', {
        "message_type": "user_account_create_fail",
                'username': username,
                'message': "Couldn't create account due to an internal error."
            }, scope = "private", user_id = username)
