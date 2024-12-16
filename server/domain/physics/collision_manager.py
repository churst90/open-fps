# domain/physics/collision_manager.py
from typing import Tuple

class CollisionManager:
    def __init__(self, map_repository, user_repository=None, ai_repository=None):
        self.map_repository = map_repository
        self.user_repository = user_repository
        self.ai_repository = ai_repository
        self.epsilon = 0.0001  # optional small epsilon if needed

    async def is_valid_position(self, map_name: str, position: tuple) -> bool:
        game_map = await self.map_repository.load_map(map_name)
        if not game_map:
            return False

        if not self._within_bounds(game_map.map_size, position):
            return False

        if not self._is_position_walkable(game_map, position):
            return False

        if self.user_repository and not await self._is_not_occupied_by_user(game_map, position):
            return False

        if self.ai_repository and not await self._is_not_occupied_by_ai(game_map, position):
            return False

        return True

    def _within_bounds(self, map_size, position):
        x_min, x_max, y_min, y_max, z_min, z_max = map_size
        x, y, z = position
        return (x_min - self.epsilon <= x <= x_max + self.epsilon and
                y_min - self.epsilon <= y <= y_max + self.epsilon and
                z_min - self.epsilon <= z <= z_max + self.epsilon)

    def _is_position_walkable(self, game_map, position):
        # If tiles represent discrete blocks, we might check if position falls within a wall tile.
        # Here we assume tile positions define exact volumes.
        # If a tile occupies (x1,x2,y1,y2,z1,z2) and is_wall=True,
        # if the point is inside that volume, it's blocked.
        x, y, z = position
        for tile in game_map.tiles.values():
            x1, x2, y1, y2, z1, z2 = tile.tile_position
            if (x1 <= x <= x2) and (y1 <= y <= y2) and (z1 <= z <= z2):
                if tile.is_wall:
                    return False
        return True

    async def _is_not_occupied_by_user(self, game_map, position):
        users = await self.user_repository.get_users_in_map(game_map.map_name)
        x, y, z = position
        for user in users:
            ux, uy, uz = user.position
            # Check if positions are essentially the same. If multiple users can’t occupy same spot,
            # consider an epsilon.
            if (abs(ux - x) < self.epsilon and
                abs(uy - y) < self.epsilon and
                abs(uz - z) < self.epsilon):
                return False
        return True

    async def _is_not_occupied_by_ai(self, game_map, position):
        ai_list = await self.ai_repository.get_ai_by_map(game_map.map_name)
        x, y, z = position
        for ai_entity in ai_list:
            ax, ay, az = ai_entity.position
            if (abs(ax - x) < self.epsilon and
                abs(ay - y) < self.epsilon and
                abs(az - z) < self.epsilon):
                return False
        return True
