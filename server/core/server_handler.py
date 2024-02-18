from .modules.map_manager import MapRegistry
from .modules.user_manager import UserRegistry
from .events.user_actions import UserActions
# from .events.map_actions import MapActions

class ServerHandler:
    def __init__(self, user_reg, map_reg, network, event_dispatcher, custom_logger):
        self.user_reg = user_reg
        self.map_reg = map_reg
        self.event_dispatcher = event_dispatcher
        self.network = network
#        self.map_actions = MapActions(self.network, self.map_reg, self.user_reg, self.event_dispatcher)
        self.user_actions = UserActions(self.network, self.map_reg, self.user_reg, self.event_dispatcher)

    async def handle_login(self, data, client_socket):
        # Assume login and account creation are handled here.
        username = data.get('username')
        password = data.get('password')
        await self.user_reg.login({'username': username, 'password': password}, client_socket)

    async def user_action(self, data, client_socket):
        username = data.get("username")
        action_type = data.get("action_type")
        direction = data.get('direction', '')  # Defaulting to empty string if not provided
        distance = data.get('distance', 1)  # Defaulting to 1 if not provided

        # Ensure the action can be performed
        if hasattr(self.user_actions, action_type):
            method = getattr(self.user_actions, action_type, None)
            if method and callable(method):
                await method(username, direction, distance)
            else:
                await self.network.send({"message_type": "error", "error_message": "That action is not supported"}, client_socket)
        else:
            await self.network.send({"message_type": "error", "error_message": "Action type not found"}, client_socket)

    async def handle_map_action(self, data, client_socket):
        map_name = data.get("map_name")
        action_type = data.get("action_type")
        # Additional arguments could be extracted here as needed

        # Verify that the action can be performed by checking if the method exists in MapActions
        if hasattr(self.map_actions, action_type):
            method = getattr(self.map_actions, action_type, None)
            if method and callable(method):
                # Call the method dynamically. Adjust arguments as necessary for your map action methods
                await method(map_name, **data)  # Passing map_name and other data as arguments
            else:
                await self.network.send({"message_type": "error", "error_message": "That action is not supported"}, client_socket)
        else:
            await self.network.send({"message_type": "error", "error_message": "Action type not found"}, client_socket)