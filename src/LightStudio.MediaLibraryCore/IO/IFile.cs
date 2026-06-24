using System;

namespace LightStudio.MediaLibraryCore.IO;

public interface IFile
{
    string Name { get; }
    string Path { get; }
    DateTime LastModifiedUtc { get; }
}
