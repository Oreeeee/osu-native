using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Catch.Difficulty;
using osu.Game.Rulesets.Catch.Difficulty.Skills;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Difficulty.Skills;
using osu.Game.Utils;
using osu.Native.Compiler;
using osu.Native.Structures.Difficulty;

namespace osu.Native.Objects.Difficulty;

/// <summary>
/// Represents a <see cref="CatchDifficultyCalculator"/>.
/// </summary>
public unsafe partial class CatchDifficultyCalculatorObject : IOsuNativeObject<DifficultyCalculatorContext<CatchDifficultyCalculator>>
{
    /// <summary>
    /// Creates an instance of a <see cref="CatchDifficultyCalculator"/> for the specified ruleset and beatmap.
    /// </summary>
    /// <param name="rulesetHandle">The handle of the ruleset passed into the difficulty calculator.</param>
    /// <param name="beatmapHandle">The handle of the beatmap the difficulty calculator targets.</param>
    /// <param name="nativeCatchDifficultyCalculatorPtr">A pointer to write the resulting native difficulty calculator object to.</param>
    [OsuNativeFunction]
    public static ErrorCode Create(RulesetHandle rulesetHandle, BeatmapHandle beatmapHandle,
                                   NativeCatchDifficultyCalculator* nativeCatchDifficultyCalculatorPtr)
    {
        Ruleset ruleset = rulesetHandle.Resolve();
        FlatWorkingBeatmap beatmap = beatmapHandle.Resolve();

        if (ruleset is not CatchRuleset)
            return ErrorCode.UnexpectedRuleset;

        CatchDifficultyCalculator calculator = (CatchDifficultyCalculator)ruleset.CreateDifficultyCalculator(beatmap);
        DifficultyCalculatorContext<CatchDifficultyCalculator> context = new(ruleset, beatmap, calculator);

        *nativeCatchDifficultyCalculatorPtr = new() { Handle = ManagedObjectStore.Store(context) };

        return ErrorCode.Success;
    }

    /// <summary>
    /// Calculates the difficulty attributes of the beatmap targetted by the specified difficulty calculator.
    /// </summary>
    /// <param name="calcHandle">The handle of the difficulty calculator.</param>
    /// <param name="modsHandle">The handle of the mods collection to consider. A null-handle equals to an empty mods collection.</param>
    /// <param name="nativeAttributesPtr">A pointer to write the resulting difficulty attributes to.</param>
    [OsuNativeFunction]
    public static ErrorCode Calculate(CatchDifficultyCalculatorHandle calcHandle, ModsCollectionHandle modsHandle,
                                      NativeCatchDifficultyAttributes* nativeAttributesPtr)
    {
        DifficultyCalculatorContext<CatchDifficultyCalculator> context = calcHandle.Resolve();
        Mod[] mods = modsHandle.IsNull ? [] : [.. modsHandle.Resolve().Select(x => x.ToMod(context.Ruleset))];

        CatchDifficultyAttributes attributes = (CatchDifficultyAttributes)context.Calculator.Calculate(mods);
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
    public static ErrorCode CalculateTimed(CatchDifficultyCalculatorHandle calcHandle, ModsCollectionHandle modsHandle,
                                           NativeTimedCatchDifficultyAttributes* nativeTimedAttributesBuffer, int* bufferSize)
    {
        DifficultyCalculatorContext<CatchDifficultyCalculator> context = calcHandle.Resolve();
        Mod[] mods = modsHandle.IsNull ? [] : [.. modsHandle.Resolve().Select(x => x.ToMod(context.Ruleset))];

        if (nativeTimedAttributesBuffer is null)
        {
            *bufferSize = context.Beatmap.GetPlayableBeatmap(context.Ruleset.RulesetInfo, mods).HitObjects.Count;
            return ErrorCode.BufferSizeQuery;
        }

        List<TimedDifficultyAttributes> attributes = context.Calculator.CalculateTimed(mods);
        NativeTimedCatchDifficultyAttributes[] nativeAttributes = [.. attributes.Select(x => new NativeTimedCatchDifficultyAttributes(x))];

        BufferHelper.Write(nativeAttributes, nativeTimedAttributesBuffer, bufferSize);
        return ErrorCode.Success;
    }
    /// <summary>
    /// Calculates section strain peaks used for difficulty visualisation.
    /// </summary>
    [OsuNativeFunction]
    public static ErrorCode CalculateStrains(CatchDifficultyCalculatorHandle calcHandle, ModsCollectionHandle modsHandle, double* strainsBuffer, int* bufferSize, int* seriesCount, int* seriesLength, double* startTime, double* sectionLength)
    {
        DifficultyCalculatorContext<CatchDifficultyCalculator> context = calcHandle.Resolve();
        Mod[] mods = modsHandle.IsNull ? [] : [.. modsHandle.Resolve().Select(x => x.ToMod(context.Ruleset))];

        if (strainsBuffer is null || context.PendingStrains is null)
        {
            NativeCatchStrainCalculator calculator = new NativeCatchStrainCalculator(context.Ruleset.RulesetInfo, context.Beatmap);
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


internal sealed class NativeCatchStrainCalculator : CatchDifficultyCalculator
{
    private const double section_length = 750;
    private double firstDifficultyObjectTime = double.NaN;
    private double clockRate = 1;

    public StrainCalculationResult Result { get; private set; } = new StrainCalculationResult(0, 0, []);

    public NativeCatchStrainCalculator(IRulesetInfo ruleset, IWorkingBeatmap beatmap)
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

        Movement movement = skills.OfType<Movement>().Single();
        List<double[]> series = new List<double[]>
        {
            movement.GetCurrentStrainPeaks().ToArray()
        };

        double startTime = double.IsNaN(firstDifficultyObjectTime) ? 0 : (Math.Ceiling(firstDifficultyObjectTime / section_length) * section_length - section_length) * clockRate;
        Result = new StrainCalculationResult(startTime, section_length * clockRate, series.ToArray());
        return attributes;
    }
}
