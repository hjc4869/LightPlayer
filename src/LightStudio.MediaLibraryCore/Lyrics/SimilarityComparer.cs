using LightStudio.MediaLibraryCore.Lyrics.RuntimeApi;
using LightStudio.MediaLibraryCore.Tools;
using System;
using System.Collections.Generic;

namespace LightStudio.MediaLibraryCore.Lyrics;

public class SimilarityComparer : IComparer<ExternalLrcInfo>
{
    private string _originalTitle, _originalArtist;

    public SimilarityComparer(string originalTitle, string originalArtist)
    {
        _originalArtist = originalArtist;
        _originalTitle = originalTitle;
    }

    public int Compare(ExternalLrcInfo x, ExternalLrcInfo y)
    {
        double simx = _originalTitle.Similarity(x.Title) * _originalArtist.Similarity(x.Artist);
        double simy = _originalTitle.Similarity(y.Title) * _originalArtist.Similarity(y.Artist);

        if (simx > simy)
            return -1;
        if (Math.Abs(simx - simy) < double.Epsilon)
            return 0;

        return 1;
    }
}
