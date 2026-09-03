using Behind_Bars.Utils.Saveable;
using System;

namespace Behind_Bars.Systems.Parole
{
    /// <summary>
    /// Drug categories tracked by parole urinalysis conditions.
    /// </summary>
    public enum TrackedDrugType
    {
        Unknown = 0,
        Weed = 1,
        Methamphetamine = 2,
        Cocaine = 3,
        Shrooms = 4
    }

    /// <summary>
    /// Saveable, data-only evidence that a parolee consumed a tracked drug.
    /// </summary>
    [Serializable]
    public sealed class DrugUseRecord
    {
        [SaveableField("recordId")]
        private string recordId;

        [SaveableField("drugType")]
        private TrackedDrugType drugType;

        [SaveableField("productDefinitionId")]
        private string productDefinitionId;

        [SaveableField("usedAtAbsoluteGameMinute")]
        private long usedAtAbsoluteGameMinute;

        [SaveableField("expiresAtAbsoluteGameMinute")]
        private long expiresAtAbsoluteGameMinute;

        [SaveableField("schemaVersion")]
        private int schemaVersion;

        /// <summary>Current persisted schema for newly-created records.</summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>Parameterless constructor used by both runtime serializers.</summary>
        public DrugUseRecord()
        {
            recordId = string.Empty;
            productDefinitionId = string.Empty;
            schemaVersion = CurrentSchemaVersion;
        }

        /// <summary>Creates a bounded drug-use record using native calendar minutes.</summary>
        public DrugUseRecord(
            TrackedDrugType drugType,
            string productDefinitionId,
            long usedAtAbsoluteGameMinute,
            long expiresAtAbsoluteGameMinute)
            : this()
        {
            recordId = Guid.NewGuid().ToString("N");
            this.drugType = drugType;
            this.productDefinitionId = productDefinitionId ?? string.Empty;
            this.usedAtAbsoluteGameMinute = usedAtAbsoluteGameMinute;
            this.expiresAtAbsoluteGameMinute = expiresAtAbsoluteGameMinute;
        }

        public string RecordId
        {
            get => recordId ?? string.Empty;
            set => recordId = value ?? string.Empty;
        }

        public TrackedDrugType DrugType
        {
            get => drugType;
            set => drugType = value;
        }

        public string ProductDefinitionId
        {
            get => productDefinitionId ?? string.Empty;
            set => productDefinitionId = value ?? string.Empty;
        }

        public long UsedAtAbsoluteGameMinute
        {
            get => usedAtAbsoluteGameMinute;
            set => usedAtAbsoluteGameMinute = value;
        }

        public long ExpiresAtAbsoluteGameMinute
        {
            get => expiresAtAbsoluteGameMinute;
            set => expiresAtAbsoluteGameMinute = value;
        }

        public int SchemaVersion
        {
            get => schemaVersion;
            set => schemaVersion = value;
        }

        /// <summary>Returns true when the record contains a supported category and valid interval.</summary>
        public bool IsStructurallyValid()
        {
            return !string.IsNullOrWhiteSpace(RecordId) &&
                   drugType != TrackedDrugType.Unknown &&
                   usedAtAbsoluteGameMinute >= 0L &&
                   expiresAtAbsoluteGameMinute > usedAtAbsoluteGameMinute;
        }

        /// <summary>Returns true until the native calendar reaches the exclusive expiration minute.</summary>
        public bool IsActiveAt(long absoluteGameMinute)
        {
            return IsStructurallyValid() &&
                   absoluteGameMinute >= usedAtAbsoluteGameMinute &&
                   absoluteGameMinute < expiresAtAbsoluteGameMinute;
        }
    }
}
