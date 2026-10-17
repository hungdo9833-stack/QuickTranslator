using System.Collections.Immutable;

namespace QuickTranslator.AiEditor.Core.Importing;

internal static class BoundaryPlanner
{
    public static ImmutableArray<PlannedSourceRegion> Plan(RawSourceSnapshot source, ImmutableArray<SourceLine> lines,
        ImmutableArray<HeadingCandidate> candidates)
    {
        if (source.Text.Length == 0) return [];
        int[] included = candidates.Select((candidate, index) => (candidate, index))
            .Where(x => x.candidate.Decision != CandidateDecision.Rejected)
            .OrderBy(x => x.candidate.HeadingSpan.StartUtf16)
            .Select(x => x.index).ToArray();
        if (included.Length == 0)
            return [new(PlannedRegionKind.Unclassified, new(0, source.Text.Length), null,
                new(0, source.Text.Length), null, null, "", null)];

        var regions = ImmutableArray.CreateBuilder<PlannedSourceRegion>(included.Length + 1);
        int firstStart = candidates[included[0]].HeadingSpan.StartUtf16;
        if (firstStart > 0)
            regions.Add(new(PlannedRegionKind.Unclassified, new(0, firstStart), null,
                new(0, firstStart), null, null, "", null));

        string? currentVolume = null;
        string? currentVolumeIdentity = null;
        for (int position = 0; position < included.Length; position++)
        {
            int candidateIndex = included[position];
            HeadingCandidate candidate = candidates[candidateIndex];
            SourceLine line = lines[candidate.LineNumber - 1];
            int start = candidate.HeadingSpan.StartUtf16;
            int end = position + 1 < included.Length ? candidates[included[position + 1]].HeadingSpan.StartUtf16 : source.Text.Length;
            int bodyStart = line.FullSpan.EndUtf16;
            if (bodyStart > end) throw new InvalidDataException("Hai tiêu đề chapter bị chồng lấn.");

            PlannedRegionKind kind = candidate.Kind switch
            {
                ChapterHeadingKind.Chapter => PlannedRegionKind.Chapter,
                ChapterHeadingKind.Volume => PlannedRegionKind.Volume,
                ChapterHeadingKind.SpecialChapter => PlannedRegionKind.SpecialChapter,
                _ => throw new ArgumentOutOfRangeException()
            };
            if (kind == PlannedRegionKind.Volume)
            {
                currentVolume = string.IsNullOrWhiteSpace(candidate.Title) ? candidate.RawHeading.Trim() : candidate.Title;
                currentVolumeIdentity = $"volume:{candidate.HeadingSpan.StartUtf16}:{candidate.Number?.ToString() ?? "-"}";
            }
            regions.Add(new(kind, new(start, end - start), candidate.HeadingSpan,
                new(bodyStart, end - bodyStart), candidateIndex, candidate.Number, candidate.Title, currentVolume)
            { VolumeIdentity = currentVolumeIdentity });
        }
        return regions.ToImmutable();
    }
}
