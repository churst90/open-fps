# domain/physics/physics_engine.py
from typing import Tuple
from domain.physics.map_physics import MapPhysics

class PhysicsEngine:
    def __init__(self):
        # Remove gravity from constructor since we'll get it from map_physics
        pass

    def apply_gravity(self, position: Tuple[float, float, float], velocity: Tuple[float,float,float], map_physics: MapPhysics, delta_time: float = 0.016) -> Tuple[Tuple[float,float,float], Tuple[float,float,float]]:
        x, y, z = position
        vx, vy, vz = velocity
        # Use map_physics.gravity
        vy += map_physics.gravity * delta_time
        new_y = y + vy * delta_time
        return (x, new_y, z), (vx, vy, vz)

    def jump(self, position: Tuple[float,float,float], velocity: Tuple[float,float,float], map_physics: MapPhysics, jump_speed: float = 5.0) -> Tuple[Tuple[float,float,float], Tuple[float,float,float]]:
        # map_physics could also adjust jump_speed if desired, but for now we just use given jump_speed
        x, y, z = position
        vx, vy, vz = velocity
        vy = jump_speed
        return (x, y, z), (vx, vy, vz)

    def apply_force(self, position: Tuple[float,float,float], velocity: Tuple[float,float,float], force: Tuple[float,float,float], map_physics: MapPhysics, mass: float = 1.0, delta_time: float = 0.016) -> Tuple[Tuple[float,float,float], Tuple[float,float,float]]:
        x, y, z = position
        vx, vy, vz = velocity
        Fx, Fy, Fz = force

        # We could factor in air_resistance or friction from map_physics if desired
        # For now, just apply force
        ax, ay, az = Fx/mass, Fy/mass, Fz/mass
        vx += ax * delta_time
        vy += ay * delta_time
        vz += az * delta_time

        new_x = x + vx * delta_time
        new_y = y + vy * delta_time
        new_z = z + vz * delta_time
        return (new_x, new_y, new_z), (vx, vy, vz)

    def check_collisions_and_adjust(self, position: Tuple[float,float,float]) -> Tuple[float,float,float]:
        return position
