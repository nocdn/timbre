using whisper_windows.Models;

namespace whisper_windows.Interfaces;

public interface ITranscriptionClientFactory
{
    ITranscriptionClient GetClient(TranscriptionProvider provider);
}
