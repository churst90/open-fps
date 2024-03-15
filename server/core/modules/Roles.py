class Role:
    def __init__(self, name, permissions):
        self.name = name
        self.permissions = permissions

    # Define roles. I'll change these later
    developer_permissions = ["manage_users", "change_map", "immune_to_health_changes", "all"]
    admin_permissions = ["manage_users", "change_map"]
    player_permissions = []

    # Possibly store roles in a more accessible way, like a dictionary
    roles = {
        "developer": developer_role,
        "admin": admin_role,
        "player": player_role
    }
