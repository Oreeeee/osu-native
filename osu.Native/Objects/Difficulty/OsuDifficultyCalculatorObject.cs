using osu.Native.Objects.Difficulty;

internal sealed class NativeOsuStrainCalculator : OsuDifficultyCalculator
{
    private const double section_length = 400;
    private DifficultyHitObject[] difficultyObjects = [];
    private double firstDifficultyObjectTime = double.NaN;
    private double clockRate = 1;

    public StrainCalculationResult Result { get; private set; } = new StrainCalculationResult(0, 0, []);

    public NativeOsuStrainCalculator(IRulesetInfo ruleset, IWorkingBeatmap beatmap)
        : base(ruleset, beatmap)
    {
    }

    protected override Skill[] CreateSkills(IBeatmap beatmap, Mod[] mods)
    {
        difficultyObjects = [];
        firstDifficultyObjectTime = double.NaN;
        clockRate = ModUtils.CalculateRateWithMods(mods);
        return base.CreateSkills(beatmap, mods);
    }

    protected override IEnumerable<DifficultyHitObject> CreateDifficultyHitObjects(IBeatmap beatmap, Mod[] mods)
    {
        difficultyObjects = base.CreateDifficultyHitObjects(beatmap, mods).ToArray();

        if (difficultyObjects.Length > 0)
            firstDifficultyObjectTime = difficultyObjects[0].StartTime;

        return difficultyObjects;
    }

    protected override DifficultyAttributes CreateDifficultyAttributes(IBeatmap beatmap, Mod[] mods, Skill[] skills)
    {
        DifficultyAttributes attributes = base.CreateDifficultyAttributes(beatmap, mods, skills);

        Aim aim = skills.OfType<Aim>().Single(x => x.IncludeSliders);
        Aim aimNoSliders = skills.OfType<Aim>().Single(x => !x.IncludeSliders);
        Speed speed = skills.OfType<Speed>().Single();
        Reading reading = skills.OfType<Reading>().Single();
        Flashlight flashlight = skills.OfType<Flashlight>().SingleOrDefault();

        double calculationStartTime = double.IsNaN(firstDifficultyObjectTime) ? 0 : Math.Ceiling(firstDifficultyObjectTime / section_length) * section_length - section_length;

        List<double[]> series = new List<double[]>
        {
            BuildStrainSeries(aim, difficultyObjects, calculationStartTime, section_length),
            BuildStrainSeries(aimNoSliders, difficultyObjects, calculationStartTime, section_length),
            BuildStrainSeries(speed, difficultyObjects, calculationStartTime, section_length),
            BuildStrainSeries(reading, difficultyObjects, calculationStartTime, section_length)
        };

        if (flashlight != null)
            series.Add(BuildStrainSeries(flashlight, difficultyObjects, calculationStartTime, section_length));

        Result = new StrainCalculationResult(calculationStartTime * clockRate, section_length * clockRate, series.ToArray());
        return attributes;
    }

    private static double[] BuildStrainSeries(Skill skill, DifficultyHitObject[] objects, double startTime, double sectionLength)
    {
        IReadOnlyList<double> values = skill.GetObjectDifficulties();

        if (objects.Length == 0 || values.Count == 0)
            return [];

        int length = Math.Max(1, (int)Math.Floor((objects[objects.Length - 1].StartTime - startTime) / sectionLength) + 1);
        double[] result = new double[length];
        int count = Math.Min(objects.Length, values.Count);

        for (int i = 0; i < count; i++)
        {
            int section = (int)Math.Floor((objects[i].StartTime - startTime) / sectionLength);

            if (section >= 0 && section < result.Length)
                result[section] = Math.Max(result[section], values[i]);
        }

        return result;
    }
}