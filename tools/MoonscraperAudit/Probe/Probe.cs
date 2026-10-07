using System;
using System.IO;
using System.Linq;
using MoonscraperChartEditor.Song;
using MoonscraperChartEditor.Song.IO;
using UnityEngine;

public static class MoonscraperPreservationProbe
{
    private static bool _ran;

    public static void Run()
    {
        var root = Environment.GetEnvironmentVariable("MOONSCRAPER_AUDIT_OUTPUT");
        if (_ran || string.IsNullOrEmpty(root) || Time.frameCount < 30) return;
        _ran = true;
        try
        {
            var callback = MidReader.CallbackState.None;
            var song = MidReader.ReadMidi(Path.Combine(root, "input.mid"), ref callback);
            Dump(song, Path.Combine(root, "midi-import.tsv"));
            ExportMidi(song, Path.Combine(root, "output.mid"));
            ExportChart(song, Path.Combine(root, "midi-export.chart"), false);
            var reopened = MidReader.ReadMidi(Path.Combine(root, "output.mid"), ref callback);
            Dump(reopened, Path.Combine(root, "midi-reimport.tsv"));

            var chart = ChartReader.ReadChart(Path.Combine(root, "input.chart"));
            Dump(chart, Path.Combine(root, "chart-import.tsv"));
            ExportChart(chart, Path.Combine(root, "output.chart"), false);
            Dump(ChartReader.ReadChart(Path.Combine(root, "output.chart")), Path.Combine(root, "chart-reimport.tsv"));

            var commands = MidReader.ReadMidi(Path.Combine(root, "commands.mid"), ref callback);
            Dump(commands, Path.Combine(root, "commands-import.tsv"));
            ExportChart(commands, Path.Combine(root, "commands-export.chart"), false);
            ExportChart(commands, Path.Combine(root, "commands-save.chart"), true);
            Dump(ChartReader.ReadChart(Path.Combine(root, "commands-save.msce")), Path.Combine(root, "msce-reimport.tsv"));
            File.WriteAllText(Path.Combine(root, "complete.txt"), "Official import/export methods completed in Unity " + Application.unityVersion + "\n");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(root, "failure.txt"), error.ToString());
        }
        Application.Quit();
    }

    private static void ExportMidi(Song song, string path)
    {
        var options = song.defaultExportOptions;
        options.format = ExportOptions.Format.Midi;
        MidWriter.WriteToFile(path, song, options);
    }

    private static void ExportChart(Song song, string path, bool generalSave)
    {
        var options = song.defaultExportOptions;
        options.isGeneralSave = generalSave;
        new ChartWriter(path).Write(song, options, out var report);
        File.WriteAllText(path + ".report.txt", "FileType=" + report.resultantFileType + "\n" + report.GetFullReport());
        if (report.HasErrors) throw new InvalidOperationException(report.GetFullReport());
    }

    private static void Dump(Song song, string path)
    {
        using (var writer = new StreamWriter(path))
        {
            writer.WriteLine("track\ttype\ttick\tnote_or_text\tlength");
            foreach (var item in song.eventsAndSections) writer.WriteLine("EVENTS\ttext\t" + item.tick + "\t" + item.title + "\t0");
            foreach (var chart in song.unrecognisedCharts.Concat(new[] { song.GetChart(Song.Instrument.Guitar, Song.Difficulty.Expert) }))
            {
                foreach (var item in chart.chartObjects)
                {
                    if (item is Note note) writer.WriteLine(chart.name + "\tnote\t" + note.tick + "\t" + note.rawNote + "\t" + note.length);
                    if (item is ChartEvent text) writer.WriteLine(chart.name + "\ttext\t" + text.tick + "\t" + text.eventName + "\t0");
                }
            }
        }
    }
}
