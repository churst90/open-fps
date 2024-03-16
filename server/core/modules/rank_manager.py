class RankManager:
    def __init__(self):
        self.rank_requirements = {
            "Newbie": {"xp_required": 0, "next_rank": "Apprentice", "reward": "100 Gold"},
            "Apprentice": {"xp_required": 1000, "next_rank": "Warrior", "reward": "200 Gold"},
            "Warrior": {"xp_required": 3000, "next_rank": "Knight", "reward": "500 Gold"},
            "Knight": {"xp_required": 6000, "next_rank": "Lord", "reward": "Sword of Valor"},
            "Lord": {"xp_required": 10000, "next_rank": "Legend", "reward": "Armor of the Sun"},
            "Legend": {"xp_required": None, "next_rank": None, "reward": None},  # Top rank
        }

    def check_rank_up(self, user):
        current_rank = user.rank
        if current_rank in self.rank_requirements and user.xp >= self.rank_requirements[current_rank]["xp_required"]:
            new_rank = self.rank_requirements[current_rank]["next_rank"]
            reward = self.rank_requirements[current_rank]["reward"]
            user.rank = new_rank
            print(f"{user.username} has been promoted to {new_rank}! Reward: {reward}")
            # Handle reward allocation here (e.g., update user inventory or stats)
