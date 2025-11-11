using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Behind_Bars.Helpers;
using static Behind_Bars.Systems.NPCs.ParoleOfficerBehavior;
using Vector3 = UnityEngine.Vector3;

namespace Behind_Bars.Systems.NPCs
{
    public static class PresetParoleOfficerRoutes
    {
        private static Dictionary<string, PatrolRoute> AllRoutes = new Dictionary<string, PatrolRoute>();
        public static PatrolRoute PoliceStation;
        public static PatrolRoute North;
        public static PatrolRoute East;
        public static PatrolRoute West;
        public static PatrolRoute Canal;
        
        public static void InitializePatrolPoints()
        {
            PoliceStation = new PatrolRoute();
            North = new PatrolRoute();
            East = new PatrolRoute();
            West = new PatrolRoute();
            Canal = new PatrolRoute();
            
            PoliceStation.points = new Vector3[]
            {
                new Vector3(27.0941f, 1.065f, 45.0492f),
                new Vector3(36.1116f, 1.065f, 45.1833f),
                new Vector3(35.58f, 1.0632f, 15.7914f),
                new Vector3(27.3605f, 1.0632f, 15.7898f)
            };

            North.points = new Vector3[]
            {
                new Vector3(25.37735f, 1.0649991f, 47.607037f),
                new Vector3(26.345558f, 1.0650015f, 56.787968f),
                new Vector3(36.26615f, 1.0650008f, 56.939613f),
                new Vector3(35.579433f, 1.0649998f, 87.01422f),
                new Vector3(66.61797f, 1.0649991f, 87.64737f),
                new Vector3(65.70307f, 1.0650003f, 55.39496f),
                new Vector3(36.909092f, 1.0649992f, 54.849365f),
                new Vector3(36.188465f, 1.0649998f, 46.32141f),
                new Vector3(27.13385f, 1.0649998f, 46.760532f)
            };

            East.points = new Vector3[]
            {
                new Vector3(27.594246f, 1.0650014f, 44.899174f),
                new Vector3(35.519417f, 1.0650004f, 44.76033f),
                new Vector3(35.58385f, 1.063165f, 15.095608f),
                new Vector3(87.511406f, 1.064999f, 16.74765f),
                new Vector3(95.70764f, 1.064999f, 16.806124f),
                new Vector3(97.74676f, 1.0649991f, 7.3822584f),
                new Vector3(138.44595f, 1.0650008f, 7.6563783f),
                new Vector3(152.34288f, 0.74026185f, -1.7282566f),
                new Vector3(140.86993f, 1.0599507f, -21.17842f),
                new Vector3(124.44148f, 0.820016f, -25.475565f),
                new Vector3(95.79805f, 1.0649998f, -18.553446f)
            };

            West.points = new Vector3[]
            {
                new Vector3(-14.404979f, 1.065001f, 91.47709f),
                new Vector3(-22.770346f, 1.0649998f, 87.04605f),
                new Vector3(-54.02713f, 1.0649996f, 87.21228f),
                new Vector3(-53.95859f, 1.0649998f, 62.472332f),
                new Vector3(-102.250656f, -2.931936f, 79.82253f),
                new Vector3(-105.46762f, -2.931926f, 72.77154f),
                new Vector3(-121.85336f, -2.9350011f, 66.127815f),
                new Vector3(-129.78345f, -2.935001f, 66.578f),
                new Vector3(-129.74121f, -2.9350002f, 81.060104f),
                new Vector3(-128.21101f, -2.9349997f, 88.770515f),
                new Vector3(-149.78374f, -2.9350002f, 96.56543f),
                new Vector3(-158.12265f, -2.9350002f, 96.39367f),
                new Vector3(-157.88808f, -2.9349997f, 43.825146f),
                new Vector3(-137.84964f, -2.935f, 24.667608f),
                new Vector3(-126.00327f, -2.9350002f, 25.092127f),
                new Vector3(-121.72268f, -2.9350004f, 77.99048f)
            };

            // Canal and Mollys routes share the same waypoints
            Canal.points =  new Vector3[]
            {
                new Vector3(-14.025592f, 1.0650002f, 33.003468f),
                new Vector3(-14.756893f, 1.0650005f, 4.86128f),
                new Vector3(-29.167412f, 0.7405812f, 2.6530232f),
                new Vector3(-33.508945f, -1.5350001f, 3.029976f),
                new Vector3(-34.02334f, -1.5350001f, 43.156326f),
                new Vector3(-66.43616f, -1.5350001f, 42.891468f),
                new Vector3(-67.72117f, -1.5350001f, 35.768265f),
                new Vector3(-76.28769f, -1.5350002f, 43.99876f),
                new Vector3(-82.37893f, -2.910005f, 52.774216f),
                new Vector3(-82.724365f, -2.9100015f, 76.6271f),
                new Vector3(-82.32376f, -2.9099987f, 96.892044f),
                new Vector3(-82.03468f, -2.9099965f, 111.522194f),
                new Vector3(-72.91779f, -2.935001f, 119.49564f),
                new Vector3(-59.80714f, -2.934999f, 121.18263f),
                new Vector3(-48.908848f, -2.9350002f, 126.49422f),
                new Vector3(-43.674164f, -2.9349995f, 126.6594f),
                new Vector3(-44.076378f, -2.9349995f, 134.7875f),
                new Vector3(-53.1527f, -2.935001f, 134.53568f),
                new Vector3(-54.553772f, -3.0349994f, 148.88174f),
                new Vector3(-63.271603f, -3.034999f, 147.56918f),
                new Vector3(-63.36554f, -4.0349994f, 168.62167f),
                new Vector3(-42.662586f, -2.9350004f, 167.04312f),
                new Vector3(-25.5552f, -3.034878f, 159.84557f),
                new Vector3(-21.147387f, -2.9562182f, 151.7426f),
                new Vector3(-21.58927f, -2.9350007f, 134.78357f),
                new Vector3(-15.290705f, -2.9349997f, 129.60294f),
                new Vector3(-14.808385f, 1.0650002f, 102.68614f),
                new Vector3(-16.190866f, 0.9749994f, 99.67507f),
                new Vector3(-12.698946f, 1.0649976f, 90.56838f),
                new Vector3(5.297567f, 0.9662206f, 92.046005f),
                new Vector3(7.4091825f, 1.0649998f, 81.1296f),
                new Vector3(27.769424f, 1.0649999f, 80.90822f),
                new Vector3(27.875011f, 1.0650004f, 58.388645f),
                new Vector3(24.999777f, 1.0650002f, 55.257977f),
                new Vector3(25.926865f, 1.0649992f, 47.038963f)
            };
            AddRoute("PoliceStation", PoliceStation);
            AddRoute("North", North);
            AddRoute("East", East);
            AddRoute("West", West);
            AddRoute("Canal", Canal);
        }

        public static PatrolRoute GetRoute(string name) { return AllRoutes[name]; }

        public static void AddRoute(string name, PatrolRoute route) {
            if (AllRoutes.ContainsKey(name)) {
                ModLogger.Warn($"Route {name} already exists, skipping!");
                return;
            }
            route.routeName = name;
            AllRoutes.Add(name, route); 
            ModLogger.Debug($"Added route {name} to Parole Officer Routes");
        }

        public static void AddRoute(string name, Vector3[] waypoints)
        {
            PatrolRoute route = new PatrolRoute
            {
                points = waypoints
            };
            AddRoute(name, route);
        }

        public static void AddRoute(string name, Vector3[] waypoints, float speed)
        {
            PatrolRoute route = new PatrolRoute
            {
                points = waypoints,
                speed = speed
            };
            AddRoute(name, route);
        }

        public static bool DeleteRoute(string name) { return AllRoutes.Remove(name); }

        public static bool DeleteRoute(PatrolRoute route)
        {
            if (AllRoutes.ContainsValue(route))
            {
                var item = AllRoutes.First(kvp => kvp.Value == route);
                return AllRoutes.Remove(item.Key);
            }
            return false;
        }

        public static bool RouteExists(string name) { return AllRoutes.ContainsKey(name); }

        public static bool RouteExists(PatrolRoute route) { return AllRoutes.ContainsValue(route); }

        public static List<PatrolRoute> GetAllRoutes() 
        {
            return AllRoutes.Values.ToList();
        }
    }
}
