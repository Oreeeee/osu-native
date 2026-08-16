using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Difficulty.Skills;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Osu.Difficulty.Skills;
using osu.Game.Utils;
using osu.Native.Compiler;
using osu.Native.Structures.Difficulty;

namespace osu.Native.Objects.Difficulty;

public unsafe partial class OsuDifficultyCalculatorObject : IOsuNativeObject<DifficultyCalculatorContext<OsuDifficultyCalculator>>
{
    [OsuNativeFunction]
    public static ErrorCode Create(RulesetHandle rulesetHandle, BeatmapHandle beatmapHandle, NativeOsuDifficultyCalculator* nativeOsuDifficultyCalculatorPtr)
    {
        Ruleset ruleset = rulesetHandle.Resolve();
        FlatWorkingBeatmap beatmap = beatmapHandle.Resolve();

        if (ruleset is not OsuRuleset)
            return ErrorCode.UnexpectedRuleset;

        OsuDifficultyCalculator calculator = (OsuDifficultyCalculator)ruleset.CreateDifficultyCalculator(beatmap);
        DifficultyCalculatorContext<OsuDifficultyCalculator> context = new(ruleset, beatmap, calculator);

        *nativeOsuDifficultyCalculatorPtr = new() { Handle = ManagedObjectStore.Store(context) };
        return ErrorCode.Success;
    }

    [OsuNativeFunction]
    public static ErrorCode Calculate(OsuDifficultyCalculatorHandle calcHandle, ModsCollectionHandle modsHandle, NativeOsuDifficultyAttributes* nativeAttributesPtr)
    {
        DifficultyCalculatorContext<OsuDifficultyCalculator> context = calcHandle.Resolve();
        Mod[] mods = modsHandle.IsNull ? [] : [.. modsHandle.Resolve().Select(x => x.ToMod(context.Ruleset))];

        OsuDifficultyAttributes attributes = (OsuDifficultyAttributes)context.Calculator.Calculate(mods);
        *nativeAttributesPtr = new(attributes);

        return ErrorCode.Success;
    }

    [OsuNativeFunction]
    public static ErrorCode CalculateTimed(OsuDifficultyCalculatorHandle calcHandle, ModsCollectionHandle modsHandle, NativeTimedOsuDifficultyAttributes* nativeTimedAttributesBuffer, int* bufferSize)
    {
        DifficultyCalculatorContext<OsuDifficultyCalculator> context = calcHandle.Resolve();
        Mod[] mods = modsHandle.IsNull ? [] : [.. modsHandle.Resolve().Select(x => x.ToMod(context.Ruleset))];

        if (nativeTimedAttributesBuffer is null)
        {
            *bufferSize = context.Beatmap.GetPlayableBeatmap(context.Ruleset.RulesetInfo, mods).HitObjects.Count;
            return ErrorCode.BufferSizeQuery;
        }

        List<TimedDifficultyAttributes> attributes = context.Calculator.CalculateTimed(mods);
        NativeTimedOsuDifficultyAttributes[] nativeAttributes = [.. attributes.Select(x => new NativeTimedOsuDifficultyAttributes(x))];

        BufferHelper.Write(nativeAttributes, nativeTimedAttributesBuffer, bufferSize);
        return ErrorCode.Success;
    }

    [OsuNativeFunction]
    public static ErrorCode CalculateStrains(OsuDifficultyCalculatorHandle calcHandle, ModsCollectionHandle modsHandle, double* strainsBuffer, int* bufferSize, int* seriesCount, int* seriesLength, double* startTime, double* sectionLength)
    {
        DifficultyCalculatorContext<OsuDifficultyCalculator> context = calcHandle.Resolve();
        Mod[] mods = modsHandle.IsNull ? [] : [.. modsHandle.Resolve().Select(x => x.ToMod(context.Ruleset))];

        if (strainsBuffer is null || context.PendingStrains is null)
        {
            NativeOsuStrainCalculator calculator = new NativeOsuStrainCalculator(context.Ruleset.RulesetInfo, context.Beatmap);
            calculator.Calculate(mods);
            context.PendingStrains = calculator.Result;
        }

        StrainCalculationResult result = context.PendingStrains ?? new StrainCalculationResult(0, 0, []);
        ErrorCode error = result.Write(strainsBuffer, bufferSize, seriesCount, seriesLength, startTime, sectionLength);

        if (strainsBuffer is not null)
            context.PendingStrains = null;

        return error;
    }
}

internal sealed class NativeOsuStrainCalculator : OsuDifficultyCalculator
{
    private const double section_length = 400;
    private DifficultyHitObject[] difficultyObjects = [];
    private double clockRate = 1;

    public StrainCalculationResult Result { get; private set; } = new StrainCalculationResult(0, 0, []);

    public NativeOsuStrainCalculator(IRulesetInfo ruleset, IWorkingBeatmap beatmap)
        : base(ruleset, beatmap)
    {
    }

    protected override Skill[] CreateSkills(IBeatmap beatmap, Mod[] mods)
    {
        difficultyObjects = [];
        clockRate = ModUtils.CalculateRateWithMods(mods);
        return base.CreateSkills(beatmap, mods);
    }

    protected override IEnumerable<DifficultyHitObject> CreateDifficultyHitObjects(IBeatmap beatmap, Mod[] mods)
    {
        difficultyObjects = base.CreateDifficultyHitObjects(beatmap, mods).ToArray();
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

        double calculationStartTime = difficultyObjects.Length == 0 ? 0 : Math.Ceiling(difficultyObjects[0].StartTime / section_length) * section_length - section_length;

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

        if (objects.Length != values.Count)
            throw new InvalidOperationException("Difficulty object and skill difficulty counts do not match.");

        int length = Math.Max(1, (int)Math.Floor((objects[objects.Length - 1].StartTime - startTime) / sectionLength) + 1);
        double[] result = new double[length];

        for (int i = 0; i < objects.Length; i++)
        {
            int section = (int)Math.Floor((objects[i].StartTime - startTime) / sectionLength);

            if (section >= 0 && section < result.Length)
                result[section] = Math.Max(result[section], values[i]);
        }

        return result;
    }
}