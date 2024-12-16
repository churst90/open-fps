# schemas/base_message.py
from pydantic import BaseModel
from typing import Any, Dict

class BaseInnerMessage(BaseModel):
    message_type: str

class BaseMessage(BaseModel):
    client_id: str
    message: BaseInnerMessage
