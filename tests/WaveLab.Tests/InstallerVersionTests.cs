using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace WaveLab.Tests;

/// <summary>
/// The installer script and the project carry the version separately, and nothing but this makes
/// them agree.
/// </summary>
/// <remarks>
/// The failure is quiet and reaches users: <c>OutputBaseFilename</c> is built from the script's
/// number, so bumping one and not the other ships <c>DeepGroove-Setup-2.0.24.exe</c> containing
/// 2.0.23 binaries — an installer that lies about what is inside it, with a matching
/// <c>AppVersion</c> in Add/Remove Programs. Both files are checked in, so this is a plain
/// comparison rather than anything clever.
/// </remarks>
public sealed class InstallerVersionTests
{
    private static DirectoryInfo? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WaveLab.sln"))) dir = dir.Parent;
        return dir;
    }

    [Fact]
    public void TheInstallerShipsTheVersionTheProjectBuilds()
    {
        DirectoryInfo? root = RepoRoot();
        Assert.True(root != null, "WaveLab.sln should sit above the test binaries.");

        string projectPath = Path.Combine(root!.FullName, "src", "WaveLab", "WaveLab.csproj");
        string scriptPath = Path.Combine(root.FullName, "installer", "WaveLab.iss");
        Assert.True(File.Exists(projectPath), $"{projectPath} is part of the repository.");
        Assert.True(File.Exists(scriptPath), $"{scriptPath} is part of the repository.");

        Match project = Regex.Match(File.ReadAllText(projectPath), @"<Version>([^<]+)</Version>");
        Match script = Regex.Match(File.ReadAllText(scriptPath), @"#define\s+MyAppVersion\s+""([^""]+)""");
        Assert.True(project.Success, "the project should state a <Version>.");
        Assert.True(script.Success, "the installer script should define MyAppVersion.");

        string projectVersion = project.Groups[1].Value.Trim();
        string scriptVersion = script.Groups[1].Value.Trim();
        Assert.True(
            projectVersion == scriptVersion,
            $"the project builds {projectVersion} and the installer would ship it as {scriptVersion}; "
            + "bump both or neither.");
    }
}
