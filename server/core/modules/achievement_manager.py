class AchievementManager:
    def __init__(self):
        self.achievements = {
            "achievement_1": {
                "description": "My first achievment.",
                "xp_reward": 100,
                "unlocks": "map_name"
            },
            "achievment_2": {
                "description": "My second achievement",
                "xp_reward": 500,
                "unlocks": "map_name"
            }
            # Add more achievements as needed
        }
        self.user_achievements = {}  # Tracks achievements completed by users

    def check_for_achievement(self, achievement_name, user):
        # Check if the user meets the criteria for the specified achievement
        if achievement_name not in self.user_achievements.get(user.username, []):
            self.award_achievement(achievement_name, user)

    def award_achievement(self, achievement_name, user):
        if achievement_name in self.achievements:
            achievement = self.achievements[achievement_name]
            # Update user's XP and possibly inventory
            user.xp += achievement["xp_reward"]
            # Handle item unlocks or map access here
            print(f"{user.username} has earned the achievement: {achievement_name}! Reward: {achievement['xp_reward']} XP")

            # Mark this achievement as completed for the user
            if user.username not in self.user_achievements:
                self.user_achievements[user.username] = []
            self.user_achievements[user.username].append(achievement_name)
