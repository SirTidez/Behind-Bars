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
        None = 0,
        LawEnforcementVictim = 1,
        IllegalWeaponParoleViolation = 2
    }

    [Serializable]
    public sealed class CrimeEnhancement
    {
        public CrimeEnhancementKind Kind;
        public string Evidence = string.Empty;

        public CrimeEnhancement()
        {
        }

        public CrimeEnhancement(CrimeEnhancementKind kind, string evidence = null)
        {
            Kind = kind;
            Evidence = evidence ?? string.Empty;
        }

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
