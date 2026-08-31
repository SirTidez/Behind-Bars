namespace Behind_Bars.Systems
{
    /// <summary>
    /// Minimal lifecycle contract for manager-owned runtime subsystems.
    /// </summary>
    public interface ISubsystemLifecycle
    {
        /// <summary>
        /// Initializes the subsystem and its owned dependencies.
        /// </summary>
        void Initialize();

        /// <summary>
        /// Releases owned resources and unregisters runtime hooks.
        /// </summary>
        void Shutdown();
    }
}
