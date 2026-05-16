using log4net.Appender;
using log4net.Core;

namespace GI_Subtitles.Core.Logging
{
    public class InMemoryAppender : AppenderSkeleton
    {
        protected override void Append(LoggingEvent loggingEvent)
        {
            string message = loggingEvent.RenderedMessage;
            if (loggingEvent.ExceptionObject != null)
            {
                message += " | " + loggingEvent.ExceptionObject.Message;
            }
            LogBuffer.Add(
                loggingEvent.TimeStamp,
                loggingEvent.Level.DisplayName,
                message);
        }
    }
}
