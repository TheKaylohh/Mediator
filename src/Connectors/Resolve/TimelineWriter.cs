using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Mediator.Connectors.Resolve;

public enum CutTransition { Cut, Dissolve }

/// <summary>
/// One cut of the edit plan. Track 1 is the main storyline, played in order. Tracks 2+ are connected clips
/// (B-roll, titles, music) placed at <see cref="TimelineSec"/>.
/// </summary>
public sealed record EditCut(string Key, ClipInfo Clip, double InSec, double OutSec, int Track = 1, double? TimelineSec = null,
    CutTransition Transition = CutTransition.Cut, double TransitionSec = 1.0);

public sealed record TimelinePlan(string Name, FrameRate Fps, int Width, int Height, IReadOnlyList<EditCut> Cuts);

/// <summary>
/// Writes an edit plan as Final Cut Pro XML 1.9 (DaVinci Resolve: File › Import › Timeline) and as a CMX3600 EDL.
/// All times are whole frames of the sequence rate written as rationals ("75075/30000s" = 75 frames at 29.97).
/// Main-track clips sit in the spine in order; a dissolve becomes a &lt;transition&gt; centred on the cut. Other tracks
/// become connected clips on lanes (positive for video, negative for audio-only). The EDL carries track 1 only (CMX3600
/// has one video track) and lists dissolves as comments; use the FCPXML when they matter.
/// </summary>
public static class TimelineWriter
{
    public const string FcpxmlVersion = "1.9";

    public sealed record Placed(EditCut Cut, long OffsetFrames, long StartFrames, long DurationFrames);

    /// <summary>Validates and lays out the plan in sequence frames. Throws <see cref="FormatException"/> with a model-readable reason.</summary>
    public static (List<Placed> Main, List<Placed> Connected, long TotalFrames) Layout(TimelinePlan plan)
    {
        var main = new List<Placed>();
        var connected = new List<Placed>();
        long cursor = 0;
        foreach (var c in plan.Cuts)
        {
            if (c.OutSec <= c.InSec) throw new FormatException($"{c.Key}: outSec must be after inSec.");
            if (c.InSec < 0 || c.OutSec > c.Clip.DurationSec + 1e-6) throw new FormatException($"{c.Key}: {c.InSec:0.###}-{c.OutSec:0.###} s is outside {c.Clip.FileName} (0-{c.Clip.DurationSec:0.###} s).");
            if (c.Track < 1) throw new FormatException($"{c.Key}: track must be 1 or more.");
            long start = plan.Fps.Frames(c.InSec);
            long dur = plan.Fps.Frames(c.OutSec) - start;
            if (dur <= 0) throw new FormatException($"{c.Key}: shorter than one frame.");
            if (c.Track == 1)
            {
                main.Add(new Placed(c, cursor, start, dur));
                cursor += dur;
            }
            else
            {
                if (c.TimelineSec is not double at || at < 0) throw new FormatException($"{c.Key}: clips on track {c.Track} need timelineSec (where they start on the timeline).");
                connected.Add(new Placed(c, plan.Fps.Frames(at), start, dur));
            }
        }
        if (main.Count == 0) throw new FormatException("The edit needs at least one clip on track 1.");
        foreach (var (prev, next) in main.Zip(main.Skip(1)))
            if (next.Cut.Transition == CutTransition.Dissolve && plan.Fps.Frames(next.Cut.TransitionSec) > Math.Min(prev.DurationFrames, next.DurationFrames))
                throw new FormatException($"{next.Cut.Key}: the dissolve is longer than one of the clips it joins.");
        long end = Math.Max(cursor, connected.Count == 0 ? 0 : connected.Max(p => p.OffsetFrames + p.DurationFrames));
        if (end > cursor) throw new FormatException("Connected clips may not run past the end of track 1.");
        return (main, connected, cursor);
    }

    /// <summary>Frames as an FCPXML rational time in the sequence timebase: f × den / num seconds.</summary>
    public static string Time(long frames, FrameRate fps)
    {
        if (frames == 0) return "0s";
        long numerator = frames * fps.Den;
        return numerator % fps.Num == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{numerator / fps.Num}s")
            : string.Create(CultureInfo.InvariantCulture, $"{numerator}/{fps.Num}s");
    }

    public static string FrameDuration(FrameRate fps) => string.Create(CultureInfo.InvariantCulture, $"{fps.Den}/{fps.Num}s");

    public static string Fcpxml(TimelinePlan plan)
    {
        var (main, connected, total) = Layout(plan);
        var fps = plan.Fps;
        var resources = new XElement("resources",
            new XElement("format", new XAttribute("id", "r1"), new XAttribute("name", $"Mediator{plan.Height}p"),
                new XAttribute("frameDuration", FrameDuration(fps)), new XAttribute("width", plan.Width), new XAttribute("height", plan.Height)));
        var assetIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var clip in plan.Cuts.Select(c => c.Clip).DistinctBy(c => c.Id))
        {
            string id = "r" + (assetIds.Count + 2).ToString(CultureInfo.InvariantCulture);
            assetIds[clip.Id] = id;
            var asset = new XElement("asset", new XAttribute("id", id), new XAttribute("name", Path.GetFileNameWithoutExtension(clip.Path)),
                new XAttribute("start", "0s"), new XAttribute("duration", Time(fps.Frames(clip.DurationSec), fps)),
                new XAttribute("hasVideo", clip.HasVideo ? "1" : "0"), new XAttribute("hasAudio", clip.AudioStreams > 0 ? "1" : "0"));
            if (clip.HasVideo) asset.Add(new XAttribute("format", "r1"));
            if (clip.AudioStreams > 0)
            {
                asset.Add(new XAttribute("audioSources", clip.AudioStreams));
                asset.Add(new XAttribute("audioChannels", clip.AudioChannels ?? 2));
                asset.Add(new XAttribute("audioRate", clip.AudioRate ?? 48000));
            }
            asset.Add(new XElement("media-rep", new XAttribute("kind", "original-media"), new XAttribute("src", new Uri(clip.Path).AbsoluteUri)));
            resources.Add(asset);
        }

        XElement ClipElement(Placed p, long offset, int? lane) => new("asset-clip",
            new XAttribute("ref", assetIds[p.Cut.Clip.Id]), new XAttribute("name", Path.GetFileNameWithoutExtension(p.Cut.Clip.Path)),
            lane is int l ? new XAttribute("lane", l) : null,
            new XAttribute("offset", Time(offset, fps)), new XAttribute("start", Time(p.StartFrames, fps)),
            new XAttribute("duration", Time(p.DurationFrames, fps)), new XAttribute("tcFormat", "NDF"));

        var spine = new XElement("spine");
        var spineClips = new List<(Placed P, XElement El)>();
        for (int i = 0; i < main.Count; i++)
        {
            var p = main[i];
            if (i > 0 && p.Cut.Transition == CutTransition.Dissolve)
            {
                long d = fps.Frames(p.Cut.TransitionSec);
                spine.Add(new XElement("transition", new XAttribute("name", "Cross Dissolve"),
                    new XAttribute("offset", Time(p.OffsetFrames - d / 2, fps)), new XAttribute("duration", Time(d, fps))));
            }
            var el = ClipElement(p, p.OffsetFrames, null);
            spine.Add(el);
            spineClips.Add((p, el));
        }
        // A connected clip's offset is in its parent's local time: parent start + (timeline position - parent offset).
        foreach (var c in connected)
        {
            var (parent, parentEl) = spineClips.Last(s => s.P.OffsetFrames <= c.OffsetFrames);
            int lane = c.Cut.Clip.HasVideo ? c.Cut.Track - 1 : -(c.Cut.Track - 1);
            parentEl.Add(ClipElement(c, parent.StartFrames + (c.OffsetFrames - parent.OffsetFrames), lane));
        }

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("fcpxml", null, null, null),
            new XElement("fcpxml", new XAttribute("version", FcpxmlVersion),
                resources,
                new XElement("library",
                    new XElement("event", new XAttribute("name", "Mediator"),
                        new XElement("project", new XAttribute("name", plan.Name),
                            new XElement("sequence", new XAttribute("format", "r1"), new XAttribute("duration", Time(total, fps)),
                                new XAttribute("tcStart", "0s"), new XAttribute("tcFormat", "NDF"),
                                new XAttribute("audioLayout", "stereo"), new XAttribute("audioRate", "48k"),
                                spine))))));
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(new StringWriter(sb, CultureInfo.InvariantCulture), new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
            doc.Save(w);
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\"", StringComparison.Ordinal);
    }

    /// <summary>Non-drop timecode at the nominal rate (29.97 counts as 30), as edit systems expect for NDF EDLs.</summary>
    public static string Timecode(long frames, FrameRate fps, int startHour = 0)
    {
        int n = fps.Nominal;
        long f = frames % n, totalSeconds = frames / n;
        long s = totalSeconds % 60, m = totalSeconds / 60 % 60, h = totalSeconds / 3600 + startHour;
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00}:{f:00}");
    }

    public static string Edl(TimelinePlan plan)
    {
        var (main, connected, _) = Layout(plan);
        var sb = new StringBuilder();
        sb.Append("TITLE: ").Append(plan.Name.Length > 70 ? plan.Name[..70] : plan.Name).Append("\r\n");
        sb.Append("FCM: NON-DROP FRAME\r\n");
        if (connected.Count > 0) sb.Append(CultureInfo.InvariantCulture, $"* NOTE: {connected.Count} clip(s) on tracks 2+ are only in the FCPXML.\r\n");
        sb.Append("\r\n");
        int n = 1;
        foreach (var p in main)
        {
            string line = string.Create(CultureInfo.InvariantCulture,
                $"{n:000}  AX       V     C        {Timecode(p.StartFrames, plan.Fps)} {Timecode(p.StartFrames + p.DurationFrames, plan.Fps)} {Timecode(p.OffsetFrames, plan.Fps, 1)} {Timecode(p.OffsetFrames + p.DurationFrames, plan.Fps, 1)}");
            sb.Append(line).Append("\r\n");
            sb.Append("* FROM CLIP NAME: ").Append(p.Cut.Clip.FileName).Append("\r\n");
            if (p.Cut.Transition == CutTransition.Dissolve && n > 1)
                sb.Append(CultureInfo.InvariantCulture, $"* TRANSITION: dissolve {p.Cut.TransitionSec:0.##} s into this clip (see FCPXML)\r\n");
            sb.Append("\r\n");
            n++;
        }
        return sb.ToString();
    }
}
