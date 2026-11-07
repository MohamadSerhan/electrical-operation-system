using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

internal static class Program
{
    private const string ApplicationProject = "EOEMS.PowerTransformersScadaUI.V5.vbproj";
    private const string PublishedApplication = "EOEMS.PowerTransformersScadaUI.V5.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var projectDirectory = AppContext.BaseDirectory;
            var projectPath = Path.Combine(projectDirectory, ApplicationProject);
            if (!File.Exists(projectPath))
                throw new FileNotFoundException("The launcher must remain beside the EOEMS source project.", projectPath);

            var outputDirectory = Path.Combine(projectDirectory, "bin", "RunLatest", "win-x64");
            PublishLatest(projectPath, outputDirectory);
            ValidatePublishedFolder(outputDirectory);

            if (args.Contains("--publish-only", StringComparer.OrdinalIgnoreCase))
                return 0;

            var applicationPath = Path.Combine(outputDirectory, PublishedApplication);
            using var application = Process.Start(new ProcessStartInfo
            {
                FileName = applicationPath,
                WorkingDirectory = outputDirectory,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Windows did not start the published EOEMS application.");
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "The latest Electrical Operation Center version could not be built or started.\r\n\r\n" + ex.Message,
                "Electrical Operation Center",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void PublishLatest(string projectPath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        // The SDK's incremental publish clean deletes files listed in an earlier publish that are no
        // longer outputs. Guard the live authentication database so a publish can never remove it.
        var liveDatabase = Path.Combine(outputDirectory, "data", "eoems.db");
        var guardCopy = Path.Combine(outputDirectory, "data", "eoems.db.publish-guard");
        var guarded = false;
        if (File.Exists(liveDatabase))
        {
            File.Copy(liveDatabase, guardCopy, overwrite: true);
            guarded = true;
        }
        try
        {
            RunPublish(projectPath, outputDirectory);
        }
        finally
        {
            if (guarded)
            {
                if (!File.Exists(liveDatabase))
                    File.Copy(guardCopy, liveDatabase);
                File.Delete(guardCopy);
            }
        }
    }

    private static void RunPublish(string projectPath, string outputDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "publish",
            projectPath,
            "-c", "Release",
            "-r", "win-x64",
            "--self-contained", "true",
            "-p:PublishSingleFile=false",
            "-o", outputDirectory,
            "--nologo"
        })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(".NET SDK could not be started. Install the .NET 10 SDK to build the latest source.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0)
        {
            var details = new StringBuilder();
            details.AppendLine("dotnet publish failed with exit code " + process.ExitCode + ".");
            if (!string.IsNullOrWhiteSpace(stdout.Result))
                details.AppendLine(stdout.Result.Trim());
            if (!string.IsNullOrWhiteSpace(stderr.Result))
                details.AppendLine(stderr.Result.Trim());
            throw new InvalidOperationException(details.ToString());
        }
    }

    private static void ValidatePublishedFolder(string directory)
    {
        var requiredFiles = new[]
        {
            PublishedApplication,
            "appsettings.json",
            Path.Combine("Assets", "IndustrialComplexAtSunset.png"),
            Path.Combine("Assets", "TwelliumLogo.png"),
            Path.Combine("Assets", "ElectricalOperationCenter.ico"),
            Path.Combine("data", "README.txt"),
            Path.Combine("data", "eoems.seed.db"),
            "coreclr.dll",
            "hostfxr.dll"
        };
        foreach (var relativePath in requiredFiles)
        {
            var fullPath = Path.Combine(directory, relativePath);
            if (!File.Exists(fullPath))
                throw new InvalidDataException("The latest publish is incomplete; required file is missing: " + relativePath);
        }

        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "appsettings.json")));
        var sql = settings.RootElement.GetProperty("Sql");
        if (!string.Equals(sql.GetProperty("StorageMode").GetString(), "PortableSqlite", StringComparison.Ordinal) ||
            !string.Equals(sql.GetProperty("PortableDatabasePath").GetString(), @"data\eoems.db", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(sql.GetProperty("ConnectionString").GetString()) ||
            sql.GetProperty("EnableLegacyPlaintextAuthentication").GetBoolean())
            throw new InvalidDataException("Published appsettings.json does not satisfy the portable SQLite security configuration.");
    }
}
