import math
import json

# project specific imports
from include.event_dispatcher import EventDispatcher
from include.managers.collision_manager import Collision
from include.event_handlers.user_movement import UserMovement

class UserHandler:
    def __init__(self, event_dispatcher, user_service):
        self.user_service = user_service
        self.event_dispatcher = event_dispatcher
        self.setup_subscriptions()

    def setup_subscriptions(self):
        self.event_dispatcher.subscribe_internal('user_move', self.handle_user_move)
        self.event_dispatcher.subscribe_internal('user_turn', self.handle_user_turn)
        self.event_dispatcher.subscribe_internal('user_account_create_ok', self.send_initial_data)
        self.event_dispatcher.subscribe_internal('user_account_login_request', self.handle_login)
        self.event_dispatcher.subscribe_internal('user_account_logout_request', self.handle_logout)

    async def handle_login(self):
        pass

    async def handle_logout(self):
        pass

    async def send_initial_data(self, event_data):
        username = event_data['username']

        user_data = self.user_reg.get_user_instance(username).to_dict()

        await self.event_dispatcher.dispatch('user_data_response', {
            "username": username,
            "user_data": user_data
        },
        scope = "private", recipient = username)

    # method for controling the user's movement (forward, backward, left and right)
    async def handle_user_move(self, event_data):
        username = event_data['username']
        direction = event_data['direction']
        distance = event_data['distance']

        # get the instance of the user
        user = self.user_reg.get_user_instance(username)

        if user:
            # calculate the move to see if it is valid
            dx, dy, dz = await UserMovement.calculate_movement_vector(direction, distance, user.yaw, user.pitch)

            # if the move was valid, store the values in a new position tuple
            new_position = (user.position[0] + dx, user.position[1] + dy, user.position[2] + dz)

            # get the map instance of the user
            map_instance = self.map_reg.get_map(user.current_map)

            # Check if map_instance is valid before checking valid_move
            if map_instance:
                # Now perform a collision check
                valid_move, message = Collision.is_move_valid(map_instance, new_position, username)

                if valid_move:
                    # use the setter function of the user instance to update the position coordinates of the user
                    user.set_position(new_position)

                    # Dispatch a message back to the user with their updated coordinates
                    await self.event_dispatcher.dispatch("user_move", {
                        "message_type": "user_move_ok",
                    "username": username,
                    "position": new_position
                },
                scope = "map", map_id = map_instance.get_map_name())
                else:
                    await self.event_dispatch.dispatch("user_move", {
                        "message_type": "user_move_failed",
                        "username": username,
                        "error": message
                    },
                    scope = "map", map_id = map_instance.get_map_name())
            else:
                await self.event_dispatcher.dispatch("user_move", {
                    "message_type": "user_move_failed",
                    "username": username,
                    "error": "Invalid map instance"
                },
                scope = "map", map_id = map_instance.get_map_name())

    # Method for calculating the turn direction of a user
    async def handle_user_turn(self, data):
        # Extract the data from the incoming data dictionary
        username = data.get('username')
        map_name = data.get('map_name')
        turn_direction = data.get('turn_direction')

        # Get the instance of the user
        user = self.user_reg.get_user_instance(username)
        if not user:
            await self.event_dispatcher("user_turn", {"message_type": "user_turn", "error": "user not found"}, scope = "private", recipient = username)
            return

        # Define steps for turning
        yaw_step, pitch_step = 1, 1

        # adjust the yaw and pitch based on the turn direction
        if turn_direction == "left":
            user.yaw = (user.yaw - yaw_step) % 360
        elif turn_direction == "right":
            user.yaw = (user.yaw + yaw_step) % 360
        elif turn_direction == "up":
            user.pitch = max(min(user.pitch + pitch_step, 90), -90)
        elif turn_direction == "down":
            user.pitch = max(min(user.pitch - pitch_step, -90), -90)

        # dispatch a message to the user about their new facing direction
        await self.event_dispatcher.dispatch("user_turn", {
            "message_type": "user_turn",
            "username": username,
            "pitch": user.pitch,
            "yaw": user.yaw
        },
        scope = "map", map_id = map_name)