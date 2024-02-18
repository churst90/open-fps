class Weapon:
    def __init__(self, name, damage, range, accuracy, fire_rate, reload_time, ammo_capacity):
        self.name = name
        self.damage = damage
        self.range = range
        self.accuracy = accuracy
        self.fire_rate = fire_rate
        self.reload_time = reload_time
        self.ammo_capacity = ammo_capacity

    def fire(self):
        # Implement fire logic here
        pass

    def reload(self):
        # Implement reload logic here
        pass

    def calculate_damage(self):
        # Implement damage calculation logic here
        pass

    # Add any other methods or properties specific to weapons


class Gun(Weapon):
    def __init__(self, name, damage, range, accuracy, fire_rate, reload_time, ammo_capacity, ammo_size):
        super().__init__(name, damage, range, accuracy, fire_rate, reload_time, ammo_capacity)
        self.ammo_size = ammo_size

    def change_ammo(self, ammo_size):
        self.ammo_size = ammo_size
        # Implement logic to handle ammo change


class Explosive(Weapon):
    def __init__(self, name, damage, range, accuracy, fire_rate, reload_time, ammo_capacity, explosion_radius):
        super().__init__(name, damage, range, accuracy, fire_rate, reload_time, ammo_capacity)
        self.explosion_radius = explosion_radius

    def detonate(self):
        # Implement detonation logic here
        pass


# Define gun categories
class Pistol(Gun):
    def __init__(self, ammo_size):
        super().__init__("Pistol", 20, 50, 90, 2, 2, 10, ammo_size)


class Handgun(Gun):
    def __init__(self, ammo_size):
        super().__init__("Handgun", 25, 75, 85, 3, 3, 12, ammo_size)


class Shotgun(Gun):
    def __init__(self, ammo_size):
        super().__init__("Shotgun", 50, 30, 70, 1, 4, 6, ammo_size)


class Rifle(Gun):
    def __init__(self, ammo_size):
        super().__init__("Rifle", 40, 100, 80, 4, 2, 30, ammo_size)


class MachineGun(Gun):
    def __init__(self, ammo_size):
        super().__init__("Machine Gun", 35, 80, 75, 10, 5, 50, ammo_size)


# Define explosives category
class ExplosiveCategory(Explosive):
    def __init__(self, name, explosion_radius):
        super().__init__(name, 100, 0, 0, 0, 0, 1, explosion_radius)


# Define specific explosives
class HandGrenade(ExplosiveCategory):
    def __init__(self):
        super().__init__("Hand Grenade", 10)


class AtomicBomb(ExplosiveCategory):
    def __init__(self):
        super().__init__("Atomic Bomb", 100)


class SmallNuke(ExplosiveCategory):
    def __init__(self):
        super().__init__("Small Nuke", 200)


class LargeNuke(ExplosiveCategory):
    def __init__(self):
        super().__init__("Large Nuke", 500)


class C4(ExplosiveCategory):
    def __init__(self):
        super().__init__("C4", 50)