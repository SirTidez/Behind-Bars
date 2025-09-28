namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Centralized resource paths for avatar customization
    /// Based on S1API documentation and game resources
    /// </summary>
    public static class AvatarResourcePaths
    {
        /// <summary>
        /// Top clothing layer paths (from S1API)
        /// </summary>
        public static class Tops
        {
            public const string TShirt = "Avatar/Layers/Top/T-Shirt";
            public const string TuckedTShirt = "Avatar/Layers/Top/Tucked T-Shirt";
            public const string ButtonUp = "Avatar/Layers/Top/ButtonUp";
            public const string RolledButtonUp = "Avatar/Layers/Top/RolledButtonUp";
            public const string FlannelButtonUp = "Avatar/Layers/Top/FlannelButtonUp";
            public const string Hoodie = "Avatar/Layers/Top/Hoodie";
            public const string Blouse = "Avatar/Layers/Top/Blouse";
            public const string HazmatSuit = "Avatar/Layers/Top/HazmatSuit";
            public const string LabCoat = "Avatar/Layers/Top/LabCoat";

            // Special work shirts
            public const string FastFoodTShirt = "Avatar/Layers/Top/FastFood T-Shirt";
            public const string GasStationTShirt = "Avatar/Layers/Top/GasStation T-Shirt";

            // Use ButtonUp for police
            public const string PoliceShirt = ButtonUp;
        }

        /// <summary>
        /// Bottom clothing layer paths
        /// </summary>
        public static class Bottoms
        {
            public const string Jeans = "Avatar/Layers/Bottom/Jeans";
            public const string Jorts = "Avatar/Layers/Bottom/Jorts";
            public const string Shorts = "Avatar/Layers/Bottom/Shorts";
            public const string Sweatpants = "Avatar/Layers/Bottom/Sweatpants";

            // For uniforms
            public const string Pants = "Avatar/Layers/Bottom/Jeans"; // Use jeans as pants
        }

        /// <summary>
        /// Hair style paths
        /// </summary>
        public static class Hair
        {
            public const string Spiky = "Avatar/Hair/Spiky/Spiky";
            public const string Long = "Avatar/Hair/Long/Long";
        }

        /// <summary>
        /// Face layer paths
        /// </summary>
        public static class Face
        {
            public const string FaceAgitated = "Avatar/Layers/Face/Face_Agitated";
            public const string FaceHappy = "Avatar/Layers/Face/Face_Happy";
            public const string FaceNeutral = "Avatar/Layers/Face/Face_Neutral";
            public const string FaceSad = "Avatar/Layers/Face/Face_Sad";
            public const string EyesHappy = "Avatar/Layers/Face/Eyes_Happy";

            // Facial hair
            public const string Beard = "Avatar/Layers/Face/Beard";
            public const string Mustache = "Avatar/Layers/Face/Mustache";
            public const string Goatee = "Avatar/Layers/Face/Goatee";
        }

        /// <summary>
        /// Footwear accessory paths (from S1API - VERIFIED)
        /// </summary>
        public static class Footwear
        {
            public const string CombatShoes = "Avatar/Accessories/Feet/CombatShoes/CombatShoes";
            public const string DressShoes = "Avatar/Accessories/Feet/DressShoes/DressShoes";
            public const string Flats = "Avatar/Accessories/Feet/Flats/Flats";
            public const string Sandals = "Avatar/Accessories/Feet/Sandals/Sandals";
            public const string Sneakers = "Avatar/Accessories/Feet/Sneakers/Sneakers";
        }

        /// <summary>
        /// Head accessory paths (from S1API - VERIFIED)
        /// </summary>
        public static class Headwear
        {
            public const string BucketHat = "Avatar/Accessories/Head/BucketHat/BucketHat";
            public const string Cap = "Avatar/Accessories/Head/Cap/Cap";
            public const string CapFastFood = "Avatar/Accessories/Head/Cap/Cap_FastFood";
            public const string ChefHat = "Avatar/Accessories/Head/ChefHat/ChefHat";
            public const string CowboyHat = "Avatar/Accessories/Head/Cowboy/CowboyHat";
            public const string FlatCap = "Avatar/Accessories/Head/FlatCap/FlatCap";
            public const string LegendSunglasses = "Avatar/Accessories/Head/LegendSunglasses/LegendSunglasses";
            public const string Oakleys = "Avatar/Accessories/Head/Oakleys/Oakleys";
            public const string PoliceCap = "Avatar/Accessories/Head/PoliceCap/PoliceCap";  // PERFECT FOR GUARDS!
            public const string PorkpieHat = "Avatar/Accessories/Head/PorkpieHat/PorkpieHat";
            public const string RectangleFrameGlasses = "Avatar/Accessories/Head/RectangleFrameGlasses/RectangleFrameGlasses";
            public const string Respirator = "Avatar/Accessories/Head/Respirator/Respirator";
            public const string SaucePan = "Avatar/Accessories/Head/SaucePan/SaucePan";
            public const string SmallRoundGlasses = "Avatar/Accessories/Head/SmallRoundGlasses/SmallRoundGlasses";
        }

        /// <summary>
        /// Underwear layer paths
        /// </summary>
        public static class Underwear
        {
            public const string Boxers = "Avatar/Layers/Underwear/Boxers";
            public const string MaleUnderwear = "Avatar/Layers/Bottom/MaleUnderwear";
            public const string FemaleUnderwear = "Avatar/Layers/Bottom/FemaleUnderwear";
        }

        /// <summary>
        /// Body layer paths
        /// </summary>
        public static class Body
        {
            public const string Nipples = "Avatar/Layers/Top/Nipples";
            public const string UpperBodyTattoos = "Avatar/Layers/Top/UpperBodyTattoos";
            public const string ChestHair = "Avatar/Layers/Top/ChestHair1";
        }

        /// <summary>
        /// Face detail layer paths (can be used like tattoos/scars)
        /// </summary>
        public static class FaceDetails
        {
            public const string Freckles = "Avatar/Layers/Face/Freckles";
            public const string OldPersonWrinkles = "Avatar/Layers/Face/OldPersonWrinkles";
            public const string TiredEyes = "Avatar/Layers/Face/TiredEyes";
            public const string EyeShadow = "Avatar/Layers/Face/EyeShadow";
        }

        /// <summary>
        /// Neck accessory paths
        /// </summary>
        public static class Neck
        {
            public const string Necklace = "Avatar/Accessories/Neck/Necklace";
        }
    }
}