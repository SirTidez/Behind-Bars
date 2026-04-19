using System;
using Behind_Bars.Helpers;
using Behind_Bars.Utils.Saveable;
using UnityEngine;

namespace Behind_Bars.Systems.Parole
{
    /// <summary>
    /// Rapport tiers that determine officer behavior modifiers
    /// </summary>
    public enum RapportTier
    {
        Hostile,    // 0-30
        Neutral,    // 31-60
        Friendly,   // 61-80
        Trusted     // 81-100
    }

    /// <summary>
    /// Tracks the officer-player rapport score and interaction history.
    /// Persists across save/load via SaveableField attributes.
    /// </summary>
    [Serializable]
    public class OfficerRapportRecord
    {
        [SaveableField("rapportScore")]
        private float rapportScore = 50f;

        [SaveableField("totalPositiveInteractions")]
        private int totalPositiveInteractions;

        [SaveableField("totalNegativeInteractions")]
        private int totalNegativeInteractions;

        [SaveableField("lastRapportChangeGameTime")]
        private float lastRapportChangeGameTime;

        public OfficerRapportRecord()
        {
            rapportScore = 50f;
            totalPositiveInteractions = 0;
            totalNegativeInteractions = 0;
            lastRapportChangeGameTime = 0f;
        }

        /// <summary>
        /// Adjust rapport score by a delta. Positive = good, negative = bad.
        /// </summary>
        public void AdjustRapport(float delta)
        {
            float oldScore = rapportScore;
            rapportScore = Mathf.Clamp(rapportScore + delta, 0f, 100f);

            if (delta > 0f)
                totalPositiveInteractions++;
            else if (delta < 0f)
                totalNegativeInteractions++;

            ModLogger.Debug($"[RAPPORT] Adjusted rapport by {delta:+0.#;-0.#}: {oldScore:F1} -> {rapportScore:F1} (Tier: {GetRapportTier()})");
        }

        /// <summary>
        /// Get the current rapport score (0-100)
        /// </summary>
        public float GetRapportScore() => rapportScore;

        /// <summary>
        /// Set rapport score directly (used for carry-over between parole terms)
        /// </summary>
        public void SetRapportScore(float score)
        {
            rapportScore = Mathf.Clamp(score, 0f, 100f);
        }

        /// <summary>
        /// Get the rapport tier based on current score
        /// </summary>
        public RapportTier GetRapportTier()
        {
            if (rapportScore <= 30f) return RapportTier.Hostile;
            if (rapportScore <= 60f) return RapportTier.Neutral;
            if (rapportScore <= 80f) return RapportTier.Friendly;
            return RapportTier.Trusted;
        }

        /// <summary>
        /// Get the search frequency modifier based on rapport tier.
        /// Lower = fewer searches, higher = more searches.
        /// </summary>
        public float GetSearchFrequencyModifier()
        {
            switch (GetRapportTier())
            {
                case RapportTier.Hostile: return 1.3f;
                case RapportTier.Neutral: return 1.0f;
                case RapportTier.Friendly: return 0.7f;
                case RapportTier.Trusted: return 0.4f;
                default: return 1.0f;
            }
        }

        /// <summary>
        /// Get total positive interactions count
        /// </summary>
        public int GetTotalPositiveInteractions() => totalPositiveInteractions;

        /// <summary>
        /// Get total negative interactions count
        /// </summary>
        public int GetTotalNegativeInteractions() => totalNegativeInteractions;

        /// <summary>
        /// Update the last rapport change game time
        /// </summary>
        public void SetLastRapportChangeGameTime(float gameTime)
        {
            lastRapportChangeGameTime = gameTime;
        }

        /// <summary>
        /// Get the last time rapport was changed
        /// </summary>
        public float GetLastRapportChangeGameTime() => lastRapportChangeGameTime;
    }
}
