using LightStudio.MediaLibraryCore.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.Tools
{
    static public class PathExclusion
    {
        static HashSet<string> ExcludedPath;
        static PathExclusion()
        {
            var excluded = IMultiMediaPlatformIO.Instance.GetConfigurationValue<string[]>("PathExclusion");
            ExcludedPath = new HashSet<string>(excluded?? Array.Empty<string>());
        }

        static private void Save()
        {
            if (ExcludedPath.Count == 0)
            {
                IMultiMediaPlatformIO.Instance.SetConfigurationValue<string[]>("PathExclusion", null);
            }
            else
            {
                IMultiMediaPlatformIO.Instance.SetConfigurationValue<string[]>("PathExclusion", ExcludedPath.ToArray());
            }
        }

        static public string[] GetExcludedPath()
        {
            return ExcludedPath.ToArray();
        }

        static public void RemoveExcludedPath(string path)
        {
            ExcludedPath.Remove(path);
            Save();
        }

        static public void AddExcludedPath(string path)
        {
            ExcludedPath.Add(path);
            Save();
        }
    }
}
