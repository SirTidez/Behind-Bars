using System;
using Behind_Bars.Systems.Jail;

namespace Behind_Bars.Systems.CrimeTracking
{
    /// <summary>
    /// Contextual legal facts attached to one underlying charge. Enhancements never
    /// create a second native crime or a second rap-sheet record by themselves.
    /// </summary>
    public enum CrimeEnhancementKind
    {
        /// <summary>No additional contextual consequence.</summary>
        None = 0,
        /// <summary>Charge involved a law-enforcement victim.</summary>
        LawEnforcementVictim = 1,
        /// <summary>Illegal weapon found during a parole search.</summary>
        IllegalWeaponParoleViolation = 2
    }

    /// <summary>
    /// A contextual legal fact attached to an existing crime instance. Enhancements are
    /// persisted with the base charge and affect its display or penalty calculation without
    /// creating another crime record.
    /// </summary>
    [Serializable]
    public sealed class CrimeEnhancement
    {
        /// <summary>Gets or sets the kind of contextual legal consequence represented.</summary>
        public CrimeEnhancementKind Kind;

        /// <summary>Gets or sets optional evidence text, such as the related victim identifier.</summary>
        public string Evidence = string.Empty;

        /// <summary>Creates an empty enhancement for serializers and legacy save loading.</summary>
        public CrimeEnhancement()
        {
        }

        /// <summary>Creates an enhancement with its legal kind and optional evidence.</summary>
        /// <param name="kind">Contextual consequence to attach to the base crime.</param>
        /// <param name="evidence">Optional evidence value persisted with the enhancement.</param>
        public CrimeEnhancement(CrimeEnhancementKind kind, string evidence = null)
        {
            Kind = kind;
            Evidence = evidence ?? string.Empty;
        }

        /// <summary>
        /// Returns the compact player-facing label for this enhancement, or an empty string
        /// when the kind has no display label.
        /// </summary>
        public string GetDisplayLabel()
        {
            return Kind switch
            {
                CrimeEnhancementKind.LawEnforcementVictim => "Against an LEO",
                CrimeEnhancementKind.IllegalWeaponParoleViolation => "Illegal Weapon",
                _ => string.Empty
            };
        }
    }

    /// <summary>
    /// Applies the legal consequence of contextual enhancements without creating a
    /// second base charge.  The LEO enhancement uses the difference between the
    /// existing Assault and AssaultOnOfficer schedules, keeping the configured
    /// officer-assault consequence authoritative while retaining the native Assault.
    /// </summary>
    internal static class CrimeEnhancementPenaltyCalculator
    {
        /// <summary>
        /// Calculates only the fine difference between the ordinary Assault schedule and the
        /// configured AssaultOnOfficer schedule. Existing AssaultOnOfficer charges are already
        /// authoritative and therefore receive no second surcharge.
        /// </summary>
        /// <param name="crime">Charge whose enhancements are evaluated.</param>
        /// <param name="baseCrimeType">Persisted/native base type used for schedule lookup.</param>
        /// <returns>Non-negative fine surcharge in the game's configured currency units.</returns>
        internal static float GetFineSurcharge(CrimeInstance crime, string baseCrimeType)
        {
            if (crime == null || !crime.HasEnhancement(CrimeEnhancementKind.LawEnforcementVictim) ||
                string.Equals(baseCrimeType, "AssaultOnOfficer", StringComparison.Ordinal))
            {
                return 0f;
            }

            if (!string.Equals(baseCrimeType, "Assault", StringComparison.Ordinal))
            {
                return 0f;
            }

            return Math.Max(0f,
                FineCalculator.Instance.GetBaseFine("AssaultOnOfficer") -
                FineCalculator.Instance.GetBaseFine("Assault"));
        }

        /// <summary>
        /// Calculates the sentence-length difference between the ordinary Assault schedule and
        /// the configured AssaultOnOfficer schedule without creating a second base charge.
        /// </summary>
        /// <param name="crime">Charge whose enhancements are evaluated.</param>
        /// <param name="baseCrimeType">Persisted/native base type used for schedule lookup.</param>
        /// <returns>Non-negative sentence surcharge in the game's configured time units.</returns>
        internal static float GetSentenceSurcharge(CrimeInstance crime, string baseCrimeType)
        {
            if (crime == null || !crime.HasEnhancement(CrimeEnhancementKind.LawEnforcementVictim) ||
                string.Equals(baseCrimeType, "AssaultOnOfficer", StringComparison.Ordinal))
            {
                return 0f;
            }

            if (!string.Equals(baseCrimeType, "Assault", StringComparison.Ordinal))
            {
                return 0f;
            }

            var config = SentenceConfigManager.Instance;
            return Math.Max(0f,
                config.GetSentenceLength("AssaultOnOfficer") -
                config.GetSentenceLength("Assault"));
        }
    }
}
