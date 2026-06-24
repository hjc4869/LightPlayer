namespace LightStudio.MediaLibraryCore.Lyrics;

public class LrcSentence(long time, string content)
{
    public long Time = time;
    public string Content = content;

    public override string ToString()
    {
        return Time.ToString() + " ms, content: " + Content;
    }
}
