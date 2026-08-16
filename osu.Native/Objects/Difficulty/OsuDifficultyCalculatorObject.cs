using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Difficulty.Skills;
using osu.Game.Rulesets.Osu.Difficulty.Skills;
using osu.Game.Utils;
using osu.Native.Compiler;
using osu.Native.Structures.Difficulty;

namespace osu.Native.Objects.Difficulty;

/// <summary>
/// Represents a <see cref="OsuDifficultyCalculator"/>.
/// </summary>
public unsafe partial class OsuDifficultyCalculatorObject : IOsuNativeObject<DifficultyCalculatorContext<OsuDifficultyCalculator>>
{
    /// <summary>
    /// Creates an instance of a <see cref="OsuDifficultyCalculator"/> for the specified ruleset and beatmap.
    /// </summary>
    /// <param name="rulesetHandle">The handle of the ruleset passed into the difficulty calculator.</param>
    /// <param name="beatmapHandle">The handle of the beatmap the difficulty calculator targets.</param>
    /// <param name="nativeOsuDifficultyCalculatorPtr">A pointer to write the resulting native difficulty calculator object to.</param>
    [OsuNativeFunction]
    public static ErrorCode Create(RulesetHandle rulesetHandle, BeatmapHandle beatmapHandle,
                                   NativeOsuDifficultyCalculator* nativeOsuDifficultyCalculatorPtr)
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

    /// <summary>
    /// Calculates the difficulty attributes of the beatmap targetted by the specified difficulty calculator.
    /// </summary>
    /// <param name="calcHandle">The handle of the difficulty calculator.</param>
    /// <param name="modsHandle">The handle of the mods collection to consider. A null-handle equals to an empty mods collection.</param>
    /// <param name="nativeAttributesPtr">A pointer to write the resulting difficulty attributes to.</param>
    [OsuNativeFunction]
    public static ErrorCode Calculate(OsuDifficultyCalculatorHandle calcHandle, ModsCollectionHandle modsHandle,
                                      NativeOsuDifficultyAttributes* nativeAttributesPtr)
    {
        DifficultyCalculatorContext<OsuDifficultyCalculator> context = calcHandle.Resolve();
        Mod[] mods = modsHandle.IsNull ? [] : [.. modsHandle.Resolve().Select(x => x.ToMod(context.Ruleset))];

        OsuDifficultyAttributes attributes = (OsuDifficultyAttributes)context.Calculator.Calculate(mods);
        *nativeAttributesPtr = new(attributes);

        return ErrorCode.Success;
    }

    /// <summary>
    /// Calculates the timed (per-object) difficulty attributes of the beatmap targetted by the specified calculator.
    /// </summary>
    /// <param name="calcHandle">The handle of the difficulty calculator.</param>
    /// <param name="modsHandle">The handle of the mods collection to consider. A null-handle equals to an empty mods collection.</param>
    /// <param name="nativeTimedAttributesBuffer">A pointer to write the resulting timed difficulty attributes to.</param>
    /// <param name="bufferSize">The size of the provided buffer.</param>
    [OsuNativeFunction]
    public static ErrorCode CalculateTimed(OsuDifficultyCalculatorHandle calcHandle, ModsCollectionHandle modsHandle,
                                           NativeTimedOsuDifficultyAttributes* nativeTimedAttributesBuffer, int* bufferSize)
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
    /// <summary>
    /// Calculates section strain peaks used for difficulty visualisation.
    /// </summary>
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
    private double firstDifficultyObjectTime = double.NaN;
    private double clockRate = 1;

    public StrainCalculationResult Result { get; private set; } = new StrainCalculationResult(0, 0, []);

    public NativeOsuStrainCalculator(IRulesetInfo ruleset, IWorkingBeatmap beatmap)
        : base(ruleset, beatmap)
    {
    }

    protected override Skill[] CreateSkills(IBeatmap beatmap, Mod[] mods)
    {
        firstDifficultyObjectTime = double.NaN;
        clockRate = ModUtils.CalculateRateWithMods(mods);
        return base.CreateSkills(beatmap, mods);
    }

    protected override IEnumerable<DifficultyHitObject> CreateDifficultyHitObjects(IBeatmap beatmap, Mod[] mods)
    {
        DifficultyHitObject[] objects = base.CreateDifficultyHitObjects(beatmap, mods).ToArray();
        if (objects.Length > 0)
            firstDifficultyObjectTime = objects[0].StartTime;
        return objects;
    }

    protected override DifficultyAttributes CreateDifficultyAttributes(IBeatmap beatmap, Mod[] mods, Skill[] skills)
    {
        DifficultyAttributes attributes = base.CreateDifficultyAttributes(beatmap, mods, skills);

        Aim aim = skills.OfType<Aim>().Single(x => x.IncludeSliders);
        Aim aimNoSliders = skills.OfType<Aim>().Single(x => !x.IncludeSliders);
        Speed speed = skills.OfType<Speed>().Single();
        Reading reading = skills.OfType<Reading>().Single();
        Flashlight flashlight = skills.OfType<Flashlight>().SingleOrDefault();

        List<double[]> series = new List<double[]>
        {
            aim.GetCurrentStrainPeaks().ToArray(),
            aimNoSliders.GetCurrentStrainPeaks().ToArray(),
            speed.GetCurrentStrainPeaks().ToArray(),
            reading.GetCurrentStrainPeaks().ToArray()
        };

        if (flashlight != null)
            series.Add(flashlight.GetCurrentStrainPeaks().ToArray());

        double startTime = double.IsNaN(firstDifficultyObjectTime) ? 0 : (Math.Ceiling(firstDifficultyObjectTime / section_length) * section_length - section_length) * clockRate;
        Result = new StrainCalculationResult(startTime, section_length * clockRate, series.ToArray());
        return attributes;
    }
}
