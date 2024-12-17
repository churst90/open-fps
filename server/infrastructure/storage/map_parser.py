# infrastructure/storage/map_parser.py
import uuid

class MapParser:
    @classmethod
    def parse_custom_map_format_to_dict(cls, custom_format_str: str) -> dict:
        map_dict = {"tiles": {}, "zones": {}, "owners": []}
        lines = custom_format_str.splitlines()

        for line in lines:
            parts = line.split(":")
            key = parts[0]

            if key == "map_name":
                map_dict["map_name"] = parts[1]
            elif key == "tile":
                # tile:x1:x2:y1:y2:z1:z2:tile_type:is_wall
                tile_key = str(uuid.uuid4())
                tile_pos = tuple(float(v) for v in parts[1:7])
                tile_type = parts[7]
                is_wall = parts[8].lower() == "true"
                map_dict["tiles"][tile_key] = {
                    "tile_position": tile_pos,
                    "tile_type": tile_type,
                    "is_wall": is_wall
                }
            elif key == "zone":
                # zone:x1:x2:y1:y2:z1:z2:zone_label:is_safe:is_hazard
                zone_key = str(uuid.uuid4())
                zone_pos = tuple(float(v) for v in parts[1:7])
                zone_label = parts[7]
                is_safe = parts[8].lower() == "true"
                is_hazard = parts[9].lower() == "true"
                # We don't have zone_type or destinations from legacy format,
                # they can be set default later.
                map_dict["zones"][zone_key] = {
                    "zone_position": zone_pos,
                    "zone_label": zone_label,
                    "is_safe": is_safe,
                    "is_hazard": is_hazard
                }
            elif key == "map_size":
                map_dict["map_size"] = tuple(float(v) for v in parts[1:7])
            elif key == "start_position":
                map_dict["start_position"] = tuple(float(v) for v in parts[1:4])
            elif key == "owner":
                map_dict["owners"].append(parts[1])

        return map_dict

    @classmethod
    def convert_dict_to_custom_map_format(cls, map_dict: dict) -> str:
        custom_format_lines = []

        # map_name
        custom_format_lines.append(f"map_name:{map_dict.get('map_name', '')}")

        # tiles
        for tile_key, tile in map_dict.get("tiles", {}).items():
            pos_str = ":".join(str(v) for v in tile["tile_position"])
            line = f"tile:{pos_str}:{tile['tile_type']}:{tile['is_wall']}"
            custom_format_lines.append(line)

        # zones
        for zone_key, zone in map_dict.get("zones", {}).items():
            pos_str = ":".join(str(v) for v in zone["zone_position"])
            line = f"zone:{pos_str}:{zone['zone_label']}:{zone['is_safe']}:{zone['is_hazard']}"
            custom_format_lines.append(line)

        # map_size
        if "map_size" in map_dict:
            map_size_str = ":".join(str(v) for v in map_dict['map_size'])
            custom_format_lines.append(f"map_size:{map_size_str}")

        # start_position
        if "start_position" in map_dict:
            start_position_str = ":".join(str(v) for v in map_dict['start_position'])
            custom_format_lines.append(f"start_position:{start_position_str}")

        # owners
        for owner in map_dict.get("owners", []):
            custom_format_lines.append(f"owner:{owner}")

        return "\n".join(custom_format_lines)
