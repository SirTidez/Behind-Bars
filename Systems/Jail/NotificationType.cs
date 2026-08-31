namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Notification categories consumed by the jail UI notification renderer.
    /// The current renderer maps Instruction to white, Progress to green, Direction to cyan, and Warning to red text;
    /// this enum does not itself control display duration or precedence.
    /// </summary>
    public enum NotificationType
    {
        /// <summary>Player guidance or an action prompt.</summary>
        Instruction,

        /// <summary>Progress or completion feedback for an operation.</summary>
        Progress,

        /// <summary>Navigation or movement guidance.</summary>
        Direction,

        /// <summary>Attention/error feedback requiring the player's notice.</summary>
        Warning
    }
}
