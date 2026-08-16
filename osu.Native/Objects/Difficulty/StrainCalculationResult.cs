using osu.Native;

namespace osu.Native.Objects.Difficulty;

internal sealed unsafe class StrainCalculationResult
{
    public readonly double StartTime;
    public readonly double SectionLength;
    public readonly double[][] Series;

    public StrainCalculationResult(double startTime, double sectionLength, double[][] series)
    {
        StartTime = startTime;
        SectionLength = sectionLength;
        Series = series;
    }

    public ErrorCode Write(double* buffer, int* bufferSize, int* seriesCount, int* seriesLength, double* startTime, double* sectionLength)
    {
        int length = 0;
        for (int i = 0; i < Series.Length; i++)
            length = Math.Max(length, Series[i].Length);

        *seriesCount = Series.Length;
        *seriesLength = length;
        *startTime = StartTime;
        *sectionLength = SectionLength;

        int required = checked(Series.Length * length);
        if (buffer is null)
        {
            *bufferSize = required;
            return ErrorCode.BufferSizeQuery;
        }

        double[] flattened = new double[required];
        for (int i = 0; i < Series.Length; i++)
            Array.Copy(Series[i], 0, flattened, i * length, Series[i].Length);

        BufferHelper.Write(flattened, buffer, bufferSize);
        return ErrorCode.Success;
    }
}
