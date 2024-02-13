from .event_handler import EventHandler
import json

class PlayerMovement(EventHandler):
    async def handle_event(self, event_type, data):
        if event_type == "PlayerMoved":
            player_id = data['player_id']
            new_position = data['position']
            map_name = data['map_name']
            
            event_data = {
                'event': 'PlayerMoved',
                'player_id': player_id,
                'position': new_position
            }
            
            # Use the dispatcher to broadcast to all players on the map
            await self.event_dispatcher.broadcast_to_map(map_name, event_data)