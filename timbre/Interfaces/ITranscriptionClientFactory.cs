using timbre.Models;

namespace timbre.Interfaces;

public interface ITranscriptionClientFactory
{
    ITranscriptionClient GetClient(TranscriptionProvider provider);
}
