using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LightStudio.MediaLibraryCore.Tools;

static public class StringExtensions
{
    static private int Min(int a, int b, int c)
    {
        int ret = a;
        if (b < ret)
            ret = b;
        if (c < ret)
            ret = c;
        return ret;
    }

    static public double Similarity(this string original, string target)
    {
        int row = original.Length + 1, column = target.Length + 1;
        var matrix = new int[row, column];
        for (var i = 0; i < column; i++)
        {
            matrix[0, i] = i;
        }

        for (var i = 0; i < row; i++)
        {
            matrix[i, 0] = i;
        }

        var cost = 0;
        for (var i = 1; i < row; i++)
        {
            for (var j = 1; j < column; j++)
            {
                if (original[i - 1] == target[j - 1])
                    cost = 0;
                else
                    cost = 1;
                matrix[i, j] = Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1, matrix[i - 1, j - 1] + cost);
            }
        }

        double length = row > column ? row : column;
        return 1 - matrix[row - 1, column - 1] / length;
    }
}