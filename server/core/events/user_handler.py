import bcrypt
import math
import json

# project specific imports
from core.events.event_handler import EventHandler
from core.events.event_dispatcher import EventDispatcher
from core.modules.collision import Collision
from core.events.user_movement import UserMovement

class UserHandler(EventHandler):
    def __init__(self, user_reg, map_reg, event_dispatcher):
        super().__init__(event_dispatcher)
        self.user_reg = user_reg
        self.map_reg = map_reg
        self.event_dispatcher.subscribe_internal('handle_create_account', self.handle_create_account)
        self.event_dispatcher.subscribe_internal('handle_user_authentication', self.handle_user_authentication)
        self.event_dispatcher.subscribe_internal('handle_user_move', self.handle_user_move)
        self.event_dispatcher.subscribe_internal('handle_user_turn', self.handle_user_turn)

    # Method for handling user authentication (logging in and logging out)
    async def handle_user_authentication(self, data):
        # Extract the variables from the incoming data
        action = data.get('action')
        username = data.get('username')
        password = data.get('password')

        # handle logging in
        if action == 'login':
            # Load the user from disk
            user = await self.user_reg.load_user(username)
            if user is None:
                await self.event_dispatcher.dispatch("handle_login", {
                    "message_type": "login_failed",
                    "error": "User does not exist"
                },
                scope = "private", recipient = username)

            # compare and validate passwords
            stored_hashed_password = user.get_password().encode('utf-8')
            if bcrypt.checkpw(password.encode('utf-8'), stored_hashed_password):

                # Get the user data and map data to send back to the user
                user_data = user.to_dict()
                map_instance = await self.map_reg.get_map_instance(user.current_map)
                map_data = map_instance.to_dict() if map_instance else {}

                # Register the user in the user registry and dispatch the map and user data back to the client
                await self.event_dispatcher.dispatch("register_user", {
                    "message_type": "login_ok",
                    "username": username,
                    "user_instance": user,
                    "user_data": user_data,
                    "map_data": map_data
                })
            else:
                await self.event_dispatcher.dispatch("handle_login", {
                    "message_type": "login_failed",
                    "error": "Incorrect password"
                },
                scope = "private", recipient = username)

        # Handle logging out
        elif action == 'logout':
            # Verify if the user exists and is logged in before logging out
            user = await self.user_reg.get_user_instance(username)

            if user and user.logged_in:
                # Dispatch a message to deregister the user
                await self.event_dispatcher.dispatch("deregister_user", {
                    "message_type": "logout_ok",
                    "username": username
                })
            else:
                await self.event_dispatcher.dispatcher("handle_logout", {
                    "message_type": "logout_failed",
                    "username": username
            })

    # Method for handling creating a new account
    async def handle_create_account(self, data):
        username = data.get('username')
        password = data.get('password')

        try:
            user_file_path = self.user_reg.users_path / f"{username}.usr"
            if user_file_path.exists():
                raise ValueError("Username already exists")

            # Now create the new user using the user registry
            await self.user_reg.create_user(username, password)

            # Now obtain the instance for the user you just created
            user_instance = await self.user_reg.get_user_instance(username)

            # convert the user instance data to a dictionary to be sent to the user
            user_data = user_instance.to_dict()

            # Now get the map instance from the map registry
            map_instance = await self.map_reg.get_map_instance(user_instance.current_map)

            # convert the map instance data to a dictionary for sending to the user
            map_data = map_instance.to_dict() if map_instance else {}

            # Dispatch a message targeted for the client with their new account details and the initial map
            await self.event_dispatcher.dispatch("handle_create_account", {
                "message_type": "create_account_ok",
                "user_data": user_data,
                "map_data": map_data,
            },
            scope = "private", recipient = username)
        except Exception as e:
            await self.event_dispatcher.dispatch("create_account", {
                "message_type": "create_account_failed",
                "error": str(e)
            },
            scope = "private", recipient = username)

    # method for controling the user's movement (forward, backward, left and right)
    async def handle_user_move(self, username, direction, distance):

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