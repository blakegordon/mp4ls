namespace Mp4ls;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: mp4ls [-v] [--cold] <path_to_mp4_file_or_wildcard>");
            Console.WriteLine("  -v, --verbose    Show structural MP4 boxes during parsing");
            Console.WriteLine("  --cold           Force evict the file from the Windows Standby Cache before parsing");
            return 1;
        }

        bool isVerbose = args.Contains("-v") || args.Contains("--verbose");
        bool isColdRun = args.Contains("--cold");
        string? targetPattern = args.LastOrDefault(a => !a.StartsWith('-'));

        if (string.IsNullOrEmpty(targetPattern))
        {
            Console.WriteLine("No path provided.");
            return 2;
        }

        string[] filesToProcess;
        try
        {
            if (targetPattern.Contains('*') || targetPattern.Contains('?'))
            {
                string? dir = Path.GetDirectoryName(targetPattern);
                string pattern = Path.GetFileName(targetPattern);
                if (string.IsNullOrEmpty(dir)) dir = ".";
                filesToProcess = Directory.GetFiles(dir, pattern);
                if (filesToProcess.Length == 0) return 3;
            }
            else
            {
                if (!File.Exists(targetPattern)) return 4;
                filesToProcess = [targetPattern];
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            Console.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 5;
        }

        foreach (string file in filesToProcess)
        {
            if (isColdRun)
            {
                Mp4HeaderParser.Evict(file);
            }

            try
            {
                Mp4HeaderParser.Parse(file, isVerbose);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Console.WriteLine($"{file}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return 0;
    }
}
