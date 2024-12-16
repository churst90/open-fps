# schemas/messages.py

from pydantic import BaseModel
from typing import Optional, Dict, Any, Tuple, Literal

class UserAccountCreateRequest(BaseModel):
    message_type: Literal["user_account_create_request"] = "user_account_create_request"
    username: str
    password: str
    role: str = "player"
    current_map: str = "Main"

class UserAccountLoginRequest(BaseModel):
    message_type: Literal["user_account_login_request"] = "user_account_login_request"
    username: str
    password: str

class UserAccountLogoutRequest(BaseModel):
    message_type: Literal["user_account_logout_request"] = "user_account_logout_request"
    token: str

class MapCreateRequest(BaseModel):
    message_type: Literal["map_create_request"] = "map_create_request"
    username: str
    token: str
    map_name: str
    map_size: Tuple[int,int,int,int,int,int]
    start_position: Tuple[int,int,int] = (0,0,0)
    is_public: bool = True
    tiles: Dict[str, Dict[str, Any]] = {}

class UserMoveRequest(BaseModel):
    message_type: Literal["user_move_request"] = "user_move_request"
    username: str
    token: str
    direction: Tuple[float,float,float]

class ChatMessageRequest(BaseModel):
    message_type: Literal["chat_message"] = "chat_message"
    username: str
    token: str
    chat_category: Literal["private","map","global","server"]
    text: str
    recipient: Optional[str] = None
    map_name: Optional[str] = None

class MapJoinRequest(BaseModel):
    message_type: Literal["map_join_request"] = "map_join_request"
    username: str
    token: str
    map_name: str

class MapLeaveRequest(BaseModel):
    message_type: Literal["map_leave_request"] = "map_leave_request"
    username: str
    token: str

class UserJumpRequest(BaseModel):
    message_type: Literal["user_jump_request"] = "user_jump_request"
    username: str
    token: str

class MapPhysicsUpdateRequest(BaseModel):
    message_type: Literal["map_physics_update_request"] = "map_physics_update_request"
    username: str
    token: str
    map_name: str
    gravity: Optional[float] = None
    air_resistance: Optional[float] = None
    friction: Optional[float] = None

# New attributes for zone creation to handle door/travel:
# We will add these fields to the "zone_data" in map_zone_add_request.
# The map_zone_add_request itself is not a separate request type here; it's handled by generic logic.
# zone_data can contain: zone_label, zone_position, is_safe, is_hazard, zone_type (door/travel/...), destination_map, destination_coords.

# No new request class is needed because zone add is already done via map_zone_add_request.
# We'll rely on the existing structure and just handle optional fields in map_zone_add_request data.
