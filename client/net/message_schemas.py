# net/message_schemas.py
from pydantic import BaseModel, Field
from typing import Literal, Optional, Dict, Any, Tuple

class BaseClientMessage(BaseModel):
    client_id: str
    message: Dict[str, Any]

# Example request messages:
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

# Example response messages from server
class UserAccountCreateOk(BaseModel):
    message_type: Literal["user_account_create_ok"] = "user_account_create_ok"
    username: str
    role: str

class UserAccountLoginOk(BaseModel):
    message_type: Literal["user_account_login_ok"] = "user_account_login_ok"
    username: str
    token: str

# Add more schemas as needed for map_create_request, map_create_ok, etc.

# You might also create a registry similar to the server side:
MESSAGE_TYPE_TO_SCHEMA = {
    "user_account_create_request": UserAccountCreateRequest,
    "user_account_login_request": UserAccountLoginRequest,
    "user_account_create_ok": UserAccountCreateOk,
    "user_account_login_ok": UserAccountLoginOk,
    # Add more as implemented
}
