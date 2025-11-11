namespace Behind_Bars.UI
{
    /// <summary>
    /// Data structure for officer command notifications
    /// Contains all information needed to display persistent command UI
    /// </summary>
    public class OfficerCommandData
    {
        /// <summary>
        /// Type of officer giving the command (e.g., "INTAKE OFFICER", "RELEASE OFFICER")
        /// </summary>
        public string OfficerType { get; set; }

        /// <summary>
        /// The instruction text to display to the player
        /// </summary>
        public string CommandText { get; set; }

        /// <summary>
        /// Current stage number in the process
        /// </summary>
        public int CurrentStage { get; set; }

        /// <summary>
        /// Total number of stages in the process
        /// </summary>
        public int TotalStages { get; set; }

        /// <summary>
        /// Whether the officer is currently escorting the player
        /// Shows "Follow me" indicator if true
        /// </summary>
        public bool IsEscorting { get; set; }

        public OfficerCommandData(string officerType, string commandText, int currentStage, int totalStages, bool isEscorting = false)
        {
            OfficerType = officerType;
            CommandText = commandText;
            CurrentStage = currentStage;
            TotalStages = totalStages;
            IsEscorting = isEscorting;
        }
    }
}
