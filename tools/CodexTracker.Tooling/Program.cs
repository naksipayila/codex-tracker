using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexUsageTray;

namespace CodexTracker.Tooling;

internal static class Program
{
    private const string SdkVersion = "8.0.423";

    private static int Main(string[] args)
    {
        try
        {
            var repositoryRoot = FindRepositoryRoot();
            if (args.Length == 0) throw new ArgumentException("A tooling command is required.");
            switch (args[0].ToLowerInvariant())
            {
                case "verify-sdk":
                    VerifySdk(repositoryRoot);
                    break;
                case "build":
                    BuildApplication(
                        repositoryRoot,
                        GetRequiredOption(args, "--output"),
                        HasOption(args, "--self-contained"),
                        GetOption(args, "--version") ?? "1.0.0");
                    break;
                case "check":
                    Check(repositoryRoot, HasOption(args, "--skip-build"));
                    break;
                case "verify-clean":
                    VerifyClean(repositoryRoot);
                    break;
                case "release":
                    CreateRelease(repositoryRoot, GetOption(args, "--version"));
                    break;
                default:
                    throw new ArgumentException("Unknown tooling command: " + args[0]);
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void VerifySdk(string repositoryRoot)
    {
        var result = RunProcess("dotnet", new[] { "--version" }, repositoryRoot);
        if (result.ExitCode != 0 || !string.Equals(result.Output.Trim(), SdkVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Release builds require .NET SDK " + SdkVersion + ".");
    }

    private static void BuildApplication(
        string repositoryRoot,
        string outputPath,
        bool selfContained,
        string version
    )
    {
        version = NormalizeVersion(version);
        outputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory)) throw new InvalidOperationException("The output path is invalid.");
        Directory.CreateDirectory(outputDirectory);

        var publishDirectory = Path.Combine(
            Path.GetTempPath(),
            "CodexUsageTray-Publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(publishDirectory);
        try
        {
            var buildHash = NativeBuildManifest.ComputeHash(repositoryRoot);
            var arguments = new List<string>
            {
                "publish",
                Path.Combine("src", "CodexUsageTray.csproj"),
                "--configuration", "Release",
                "--runtime", "win-x64",
                "--self-contained", selfContained ? "true" : "false",
                "--output", publishDirectory,
                "-p:PublishSingleFile=true",
                "-p:IncludeNativeLibrariesForSelfExtract=" + (selfContained ? "true" : "false"),
                "-p:Version=" + version,
                "-p:InformationalVersion=build-" + buildHash,
                "-p:DebugType=None",
                "-p:DebugSymbols=false",
            };
            RunRequired("dotnet", arguments, repositoryRoot);

            var publishedExecutable = Path.Combine(publishDirectory, "CodexTracker.exe");
            if (!File.Exists(publishedExecutable))
                throw new InvalidOperationException("The native publish did not produce CodexTracker.exe.");
            File.Copy(publishedExecutable, outputPath, true);
        }
        finally
        {
            TryDeleteDirectory(publishDirectory);
        }
    }

    private static void Check(string repositoryRoot, bool skipBuild)
    {
        var projectPath = Path.Combine(repositoryRoot, "src", "CodexUsageTray.csproj");
        var testProject = Path.Combine(repositoryRoot, "tests", "native", "CodexUsageTray.NativeTests.csproj");
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "CodexUsageTray-Check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            RunRequired("dotnet", new[] { "restore", projectPath }, repositoryRoot);
            if (!skipBuild)
            {
                RunRequired("dotnet", new[] { "build", projectPath, "--configuration", "Release", "--no-restore" }, repositoryRoot);
            }
            RunRequired("dotnet", new[] { "run", "--project", testProject, "--configuration", "Release" }, repositoryRoot);

            var sourceLauncher = Path.Combine(temporaryDirectory, "source-CodexTracker.exe");
            BuildApplication(repositoryRoot, sourceLauncher, false, "1.0.0");
            RunSelfTest(sourceLauncher, repositoryRoot, temporaryDirectory);
            RunNativeSmokeTest(sourceLauncher, repositoryRoot, temporaryDirectory);
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
        Console.WriteLine("Native source and launcher checks passed.");
    }

    private static string RunSelfTest(string executable, string workingDirectory, string temporaryDirectory)
    {
        var token = Guid.NewGuid().ToString("N");
        var readyPath = Path.Combine(temporaryDirectory, "self-test-" + token + ".ready");
        try
        {
            var result = RunProcess(
                executable,
                new[] { "--self-test", "3", readyPath, token },
                workingDirectory);
            if (result.ExitCode != 0 || !File.Exists(readyPath))
                throw new InvalidOperationException(Path.GetFileName(executable) + " failed native protocol self-test.");
            var response = File.ReadAllText(readyPath).Trim();
            if (!response.StartsWith(token + "|", StringComparison.Ordinal) ||
                response.Length != token.Length + 65)
                throw new InvalidDataException(Path.GetFileName(executable) + " returned invalid self-test data.");
            return response.Substring(token.Length + 1);
        }
        finally
        {
            TryDeleteFile(readyPath);
        }
    }

    private static void RunNativeSmokeTest(string executable, string workingDirectory, string temporaryDirectory)
    {
        var token = Guid.NewGuid().ToString("N");
        var readyPath = Path.Combine(temporaryDirectory, "native-smoke-" + token + ".ready");
        try
        {
            var result = RunProcess(
                executable,
                new[] { "--native-smoke-test", readyPath, token },
                workingDirectory);
            if (result.ExitCode != 0 || !File.Exists(readyPath) ||
                !string.Equals(File.ReadAllText(readyPath).Trim(), token, StringComparison.Ordinal))
                throw new InvalidOperationException("The tracked native application failed its WPF readiness smoke test.");
        }
        finally
        {
            TryDeleteFile(readyPath);
        }
    }

    private static void VerifyClean(string repositoryRoot)
    {
        var diff = RunProcess("git", new[] { "diff", "--exit-code", "--" }, repositoryRoot);
        if (diff.ExitCode != 0) throw new InvalidOperationException("The working tree has unstaged changes.\n" + diff.Output);
        var cached = RunProcess("git", new[] { "diff", "--cached", "--exit-code", "--" }, repositoryRoot);
        if (cached.ExitCode != 0) throw new InvalidOperationException("The index has staged changes.\n" + cached.Output);
        var status = RunProcess("git", new[] { "status", "--porcelain=v1", "--untracked-files=all" }, repositoryRoot);
        if (status.ExitCode != 0 || status.Output.Trim().Length != 0)
            throw new InvalidOperationException("Release verification changed the checkout.\n" + status.Output);
    }

    private static void CreateRelease(string repositoryRoot, string requestedVersion)
    {
        var version = requestedVersion;
        if (string.IsNullOrWhiteSpace(version)) version = Environment.GetEnvironmentVariable("INPUT_VERSION");
        if (string.IsNullOrWhiteSpace(version)) version = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");
        version = NormalizeVersion(version);
        var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        if (string.IsNullOrWhiteSpace(repository)) throw new InvalidOperationException("GITHUB_REPOSITORY is missing.");

        var releaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "CodexUsageTray-Release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(releaseDirectory);
        try
        {
            var package = Path.Combine(releaseDirectory, "CodexTracker.exe");
            BuildApplication(repositoryRoot, package, true, version);
            RunSelfTest(package, repositoryRoot, releaseDirectory);
            RunNativeSmokeTest(package, repositoryRoot, releaseDirectory);

            var packageHash = ComputeFileHash(package);
            var manifestPath = Path.Combine(releaseDirectory, "latest.json");
            var manifest = new
            {
                version,
                packageUrl = "https://github.com/" + repository + "/releases/download/v" + version + "/CodexTracker.exe",
                sha256 = packageHash,
            };
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            PublishRelease(repositoryRoot, repository, version, package, manifestPath);
        }
        finally
        {
            TryDeleteDirectory(releaseDirectory);
        }
    }

    private static void PublishRelease(
        string repositoryRoot,
        string repository,
        string version,
        string package,
        string manifestPath)
    {
        var tag = "v" + version;
        var existing = RunProcess("gh", new[] { "release", "view", tag, "--repo", repository }, repositoryRoot);
        if (existing.ExitCode == 0)
        {
            RunRequired("gh", new[]
            {
                "release", "upload", tag, package, manifestPath,
                "--repo", repository,
                "--clobber",
            }, repositoryRoot);
            Console.WriteLine("Updated existing GitHub release " + tag + ".");
            return;
        }

        RunRequired("gh", new[]
        {
            "release", "create", tag, package, manifestPath,
            "--repo", repository,
            "--title", "Codex Tracker " + version,
            "--generate-notes",
        }, repositoryRoot);
    }

    private static string ComputeFileHash(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
        {
            return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
        }
    }

    private static string NormalizeVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A release version is required.");
        value = value.Trim().TrimStart('v');
        var parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(part => !int.TryParse(part, out var number) || number < 0))
            throw new ArgumentException("Version must use numeric major.minor.patch format: " + value);
        return value;
    }

    private static string FindRepositoryRoot()
    {
        var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var start in starts)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "CodexUsageTray.csproj")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the Codex Tracker repository root.");
    }

    private static ProcessResult RunProcess(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start " + fileName + ".");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (output.Length > 0) Console.Write(output);
        if (error.Length > 0) Console.Error.Write(error);
        return new ProcessResult(process.ExitCode, output + error);
    }

    private static void RunRequired(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var result = RunProcess(fileName, arguments, workingDirectory);
        if (result.ExitCode != 0) throw new InvalidOperationException(fileName + " failed with exit code " + result.ExitCode + ".");
    }

    private static string GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }

    private static string GetRequiredOption(string[] args, string name) =>
        GetOption(args, name) ?? throw new ArgumentException("Missing option: " + name);

    private static bool HasOption(string[] args, string name) =>
        args.Any(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private readonly record struct ProcessResult(int ExitCode, string Output);
}
