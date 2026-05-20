namespace YARG.Online.Game.Contracts.Enums;

// Wire-format mirror of YARG.Core.Difficulty. Values MUST match the source enum.
public enum DifficultyId : byte
{
    Beginner = 0,
    Easy = 1,
    Medium = 2,
    Hard = 3,
    Expert = 4,
    ExpertPlus = 5,
}
