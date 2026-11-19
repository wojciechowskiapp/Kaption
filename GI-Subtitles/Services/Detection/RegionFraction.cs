namespace GI_Subtitles.Services.Detection
{
    /// <summary>A rectangle as fractions of the 16:9 reference area inside the game window.</summary>
    public readonly record struct RegionFraction(double X, double Y, double W, double H);
}
