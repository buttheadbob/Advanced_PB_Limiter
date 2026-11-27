using Advanced_PB_Limiter.QuasarContracts;

namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>A chart for pbl-charts.js. Kind "runs" counts runs back from the last one, at 0; "time" has Unix seconds.</summary>
public sealed record PblChartSpec(string Kind, string Label, int Height, IReadOnlyList<double> X, IReadOnlyList<PblChartSeries> Series);

/// <summary>
/// Style "bars", "line", or marks (null where there is none): "points", "hollow" (unfilled), "solid" (larger, filled) or
/// "ring" (larger still and unfilled, drawn round a mark); or "rule", a level drawn dashed across the chart.
/// </summary>
public sealed record PblChartSeries(string Label, string Tone, string Style, IReadOnlyList<double?> Values);

public static class ChartSpecs
{
    /// <summary>A block's last runs as bars, the runs the average leaves out marked, and its average and limits as rules.</summary>
    public static PblChartSpec Runs(BlockHistoryRowView history, string label)
    {
        int count = history.RunTimesMS.Count;
        double[] x = new double[count];
        double?[] runs = new double?[count];
        double?[] leftOut = new double?[count];
        double?[] forgiven = new double?[count];
        double?[] stoodOut = new double?[count];
        double?[] counted = new double?[count];
        double?[] instantKilled = new double?[count];
        double?[] notChecked = new double?[count];
        for (int index = 0; index < count; index++)
        {
            x[index] = index - (count - 1);
            double run = history.RunTimesMS[index];
            runs[index] = run;
            switch (index < history.Outcomes.Count ? history.Outcomes[index] : RunOutcome.Under)
            {
                case RunOutcome.Forgiven:
                    forgiven[index] = run;
                    break;
                case RunOutcome.StoodOut:
                    stoodOut[index] = run;
                    break;
                case RunOutcome.Counted:
                    counted[index] = run;
                    break;
                case RunOutcome.InstantKilled:
                    instantKilled[index] = run;
                    break;
                case RunOutcome.NotChecked:
                    notChecked[index] = run;
                    break;
            }
        }
        foreach (int index in history.LeftOutOfAverage)
        {
            if (index >= 0 && index < count) leftOut[index] = history.RunTimesMS[index];
        }

        // Each outcome's marks are on the chart when the limits can give it, or a run kept it from before a limit changed:
        // the series stay the same from one refresh to the next, so the legend items hidden stay hidden. The left-out
        // ring is drawn last, round the outcome marks.
        bool runTimeLimit = history.RunTimeLimitMS > 0;
        List<PblChartSeries> series = [new("Run time", "primary", "bars", runs)];
        AddMarks(series, "Forgiven as a spike", "success", forgiven, runTimeLimit);
        AddMarks(series, "Stood out as a spike", "success", stoodOut, !runTimeLimit, "hollow");
        AddMarks(series, "Counted", "danger", counted, runTimeLimit);
        AddMarks(series, "Instant kill", "danger", instantKilled, history.InstantKillMS > 0, "solid");
        AddMarks(series, "Over the limit, not checked", "muted", notChecked, runTimeLimit);
        series.Add(new("Left out of the average", "warning", "ring", leftOut));
        series.Add(new("Average", "info", "rule", Rule(history.AverageMS, count)));
        if (history.AverageLimitMS > 0)
            series.Add(new PblChartSeries("Average limit", "warning", "rule", Rule(history.AverageLimitMS, count)));
        if (history.RunTimeLimitMS > 0)
            series.Add(new PblChartSeries("Run-time limit", "danger", "rule", Rule(history.RunTimeLimitMS, count)));
        return new PblChartSpec("runs", label, 240, x, series);
    }

    /// <summary>The page's totals over the snapshots this tab has seen.</summary>
    public static PblChartSpec Session(SessionHistory history)
    {
        IReadOnlyList<HistoryPoint> points = history.Points;
        double[] x = new double[points.Count];
        double?[] last = new double?[points.Count];
        double?[] average = new double?[points.Count];
        for (int index = 0; index < points.Count; index++)
        {
            x[index] = new DateTimeOffset(points[index].AtUtc).ToUnixTimeMilliseconds() / 1000d;
            last[index] = points[index].CombinedLast;
            average[index] = points[index].CombinedAverage;
        }
        return new PblChartSpec("time", "Total run times over this session", 220, x,
        [
            new("Total last", "primary", "line", last),
            new("Total average", "info", "line", average),
        ]);
    }

    /// <summary>How many of the block's runs ended each way, from the outcomes kept as they came.</summary>
    public static int CountOutcome(BlockHistoryRowView history, RunOutcome outcome)
    {
        int count = 0;
        foreach (RunOutcome kept in history.Outcomes)
        {
            if (kept == outcome) count++;
        }
        return count;
    }

    private static void AddMarks(List<PblChartSeries> series, string label, string tone, double?[] values, bool possible, string style = "points")
    {
        if (possible)
        {
            series.Add(new PblChartSeries(label, tone, style, values));
            return;
        }
        foreach (double? value in values)
        {
            if (value is not null)
            {
                series.Add(new PblChartSeries(label, tone, style, values));
                return;
            }
        }
    }

    private static double?[] Rule(double value, int count)
    {
        double?[] values = new double?[count];
        Array.Fill(values, value);
        return values;
    }
}
