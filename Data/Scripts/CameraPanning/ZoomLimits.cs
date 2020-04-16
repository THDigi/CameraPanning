namespace Digi.CameraPanning
{
    public class ZoomLimits
    {
        public readonly float MinFov;
        public readonly float MaxFov;

        public readonly float MinWidth;
        public readonly float MaxWidth;

        public ZoomLimits(float minFov, float maxFov)
        {
            MinFov = minFov;
            MaxFov = maxFov;

            MinWidth = CameraBlock.FovToWidth(minFov);
            MaxWidth = CameraBlock.FovToWidth(maxFov);
        }
    }
}
