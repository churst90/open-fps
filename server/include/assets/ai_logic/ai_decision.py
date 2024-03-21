class DecisionNode:
    def __init__(self, condition, trueBranch, falseBranch):
        self.condition = condition
        self.trueBranch = trueBranch
        self.falseBranch = falseBranch

    def decide(self, ai):
        if self.condition(ai):
            return self.trueBranch.decide(ai)
        else:
            return self.falseBranch.decide(ai)

class ActionNode:
    def __init__(self, action):
        self.action = action

    def decide(self, ai):
        return self.action(ai)

# Example conditions and actions
def is_player_near(ai):
    # Determine if a player is within a certain distance of the AI
    return True # Placeholder

def move_towards_player(ai):
    # Logic to move AI towards the player
    pass # Placeholder

def patrol(ai):
    # Logic for AI to follow a patrol path
    pass # Placeholder

# Constructing the decision tree
ai_decision_tree = DecisionNode(
    is_player_near,
    trueBranch=ActionNode(move_towards_player),
    falseBranch=ActionNode(patrol)
)

# In the AI update loop
action = ai_decision_tree.decide(ai_instance)
action()
