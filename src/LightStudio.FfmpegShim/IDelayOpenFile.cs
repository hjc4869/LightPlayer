using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LightStudio.FfmpegShim
{
    public interface IDelayOpenFile
    {
        Stream OpenRead();
    }
}
