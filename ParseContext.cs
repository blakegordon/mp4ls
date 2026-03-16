namespace Mp4ls;

public class ParseContext
{
    public long TotalFileSize;
    public string MajorBrand = "Unknown";
    public string BrandExplanation = "";
    public double DurationSeconds = 0;
    public double BitrateMbps = 0;
    public List<TrackInfo> Tracks = [];
    public TrackInfo? CurrentTrack;
}