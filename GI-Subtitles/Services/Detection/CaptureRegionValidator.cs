namespace GI_Subtitles.Services.Detection
{
    internal static class CaptureRegionValidator
    {
        internal static bool TryParse(string value, out int x, out int y, out int width, out int height)
            => TryParse((value ?? string.Empty).Split(','), out x, out y, out width, out height);

        internal static bool TryParse(string[] parts, out int x, out int y, out int width, out int height)
        {
            x = y = width = height = 0;
            return parts != null && parts.Length >= 4 &&
                   int.TryParse(parts[0], out x) &&
                   int.TryParse(parts[1], out y) &&
                   int.TryParse(parts[2], out width) && width > 0 &&
                   int.TryParse(parts[3], out height) && height > 0;
        }
    }
}
