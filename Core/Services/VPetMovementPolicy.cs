using static VPet_Simulator.Core.GraphInfo;

namespace VPetLLM.Core.Services
{
    /// <summary>
    /// Central policy for interactions that must not replace VPet host-controlled animations.
    /// </summary>
    internal static class VPetMovementPolicy
    {
        public static bool IsAnimationProtected(GraphType type)
        {
            return type is GraphType.Move
                or GraphType.Touch_Head
                or GraphType.Touch_Body
                or GraphType.Raised_Dynamic
                or GraphType.Raised_Static
                or GraphType.Switch_Up
                or GraphType.Switch_Down
                or GraphType.Switch_Thirsty
                or GraphType.Switch_Hunger
                or GraphType.StartUP
                or GraphType.Shutdown;
        }

        public static double ClampWindowCoordinate(
            double target,
            double areaStart,
            double areaLength,
            double windowLength)
        {
            if (!double.IsFinite(target) || !double.IsFinite(areaStart)
                || !double.IsFinite(areaLength) || !double.IsFinite(windowLength))
            {
                return double.IsFinite(areaStart) ? areaStart : 0;
            }

            var usableLength = Math.Max(0, areaLength - Math.Max(0, windowLength));
            return Math.Clamp(target, areaStart, areaStart + usableLength);
        }
    }
}
