# schemas/__init__.py
from .messages import (
    UserAccountCreateRequest,
    UserAccountLoginRequest,
    UserAccountLogoutRequest,
    MapCreateRequest,
    UserMoveRequest,
    ChatMessageRequest,
    MapJoinRequest,
    MapLeaveRequest,
    UserJumpRequest,
    MapPhysicsUpdateRequest
)

MESSAGE_TYPE_TO_SCHEMA = {
    "user_account_create_request": UserAccountCreateRequest,
    "user_account_login_request": UserAccountLoginRequest,
    "user_account_logout_request": UserAccountLogoutRequest,
    "map_create_request": MapCreateRequest,
    "user_move_request": UserMoveRequest,
    "chat_message": ChatMessageRequest,
    "map_join_request": MapJoinRequest,
    "map_leave_request": MapLeaveRequest,
    "user_jump_request": UserJumpRequest,
    "map_physics_update_request": MapPhysicsUpdateRequest
}
