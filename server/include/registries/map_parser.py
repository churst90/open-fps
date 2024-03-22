import uuid

class MapParser:

    @classmethod
    def parse_custom_map_format_to_dict(cls, custom_format_str):
        """
        Parses a custom map format string into a dictionary suitable for instantiating Map objects.
        """
        map_dict = {"tiles": {}, "zones": {}, "owners": []}
        lines = custom_format_str.splitlines()

        for line in lines:
            parts = line.split(":")
            key = parts[0]

            if key == "map_name":
                map_dict["map_name"] = parts[1]
            elif key == "tile":
                tile_key = str(uuid.uuid4())  # Generate a unique key for each tile
                map_dict["tiles"][tile_key] = {
                    "tile_position": tuple(map(int, parts[1:7])),
                    "tile_type": parts[7],
                    "is_wall": parts[8].lower() == "true"
                }
            elif key == "zone":
                zone_key = str(uuid.uuid4())  # Generate a unique key for each zone
                map_dict["zones"][zone_key] = {
                    "zone_position": tuple(map(int, parts[1:7])),
                    "zone_label": parts[7],  # Adding zone_label
                    "is_safe": parts[8].lower() == "true",
                    "is_hazard": parts[9].lower() == "true"
                }
            elif key == "map_size":
                map_dict["map_size"] = tuple(map(int, parts[1:7]))
            elif key == "start_position":
                map_dict["start_position"] = tuple(map(int, parts[1:4]))
            elif key == "owner":
                map_dict["owners"].append(parts[1])

        return map_dict

    @classmethod
    def convert_dict_to_custom_map_format(cls, map_dict):
        """
        Converts a map dictionary back into the custom map format string.
        """
        custom_format_lines = []

        custom_format_lines.append(f"map_name:{map_dict.get('map_name', '')}")

        for tile_key, tile in map_dict.get("tiles", {}).items():
            position_str = ':'.join(map(str, tile['tile_position']))
            line = f"tile:{position_str}:{tile['tile_type']}:{tile['is_wall']}"
            custom_format_lines.append(line)

        for zone_key, zone in map_dict.get("zones", {}).items():
            position_str = ':'.join(map(str, zone['zone_position']))
            # Include zone_label in the format
            line = f"zone:{position_str}:{zone['zone_label']}:{zone['is_safe']}:{zone['is_hazard']}"
            custom_format_lines.append(line)

        if "map_size" in map_dict:
            map_size_str = ':'.join(map(str, map_dict['map_size']))
            custom_format_lines.append(f"map_size:{map_size_str}")

        if "start_position" in map_dict:
            start_position_str = ':'.join(map(str, map_dict['start_position']))
            custom_format_lines.append(f"start_position:{start_position_str}")

        for owner in map_dict.get("owners", []):
            custom_format_lines.append(f"owner:{owner}")

        return "\n".join(custom_format_lines)
