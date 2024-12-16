# interfaces/serializer.py
import json
from typing import Any, Dict

def serialize_to_json(data: Any) -> str:
    """
    Serialize Python data to a JSON string.
    
    :param data: The Python data structure (dict, list, etc.).
    :return: A JSON-formatted string.
    """
    return json.dumps(data, ensure_ascii=False)

def deserialize_from_json(json_str: str) -> Any:
    """
    Deserialize a JSON string into Python data.
    
    :param json_str: A JSON-formatted string.
    :return: The corresponding Python object (dict, list, etc.).
    """
    return json.loads(json_str)

def to_dict(obj: Any) -> Dict:
    """
    Convert a domain object to a dictionary. 
    
    If all domain objects implement `.to_dict()`, 
    this function can be a simple wrapper. If not, 
    it could contain logic to introspect objects and convert them.
    """
    if hasattr(obj, "to_dict") and callable(obj.to_dict):
        return obj.to_dict()
    raise TypeError(f"Object of type {type(obj)} cannot be converted to dict using this serializer.")
