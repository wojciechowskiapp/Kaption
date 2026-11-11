using System.IO;
using GI_Subtitles.Services.Data;

namespace GI_Subtitles.Common
{
    /// <summary>
    /// <c>%kaptionDataRoot</c> in app.config's log file pattern: the app's data folder (<see cref="GameDataPaths.Root"/>),
    /// so app.log follows the end-to-end suite's isolated folder instead of always opening the real profile's.
    /// </summary>
    public sealed class DataRootPatternConverter : log4net.Util.PatternConverter
    {
        public override void Convert(TextWriter writer, object state) => writer.Write(GameDataPaths.Root);
    }
}
