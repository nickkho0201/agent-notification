using System.Diagnostics;
using AgentNotify.Launcher;
using Xunit;

namespace AgentNotify.Tests;

public class LauncherTests
{
    [Fact]
    public void Parse_RunDueWithoutHome()
    {
        var ok = LauncherArgs.TryParse(new[] { "run-due" }, out var invocation, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Null(invocation!.Home);
        Assert.Equal(new[] { "run-due" }, invocation.ChildArguments());
    }

    [Fact]
    public void Parse_RunDueWithHomeContainingSpacesAndUnicode()
    {
        const string home = @"C:\Users\Иван Петров\AppData\Local\AgentNotification";
        var ok = LauncherArgs.TryParse(new[] { "run-due", "--home", home }, out var invocation, out _);

        Assert.True(ok);
        Assert.Equal(home, invocation!.Home);
        // Аргументы дочернего процесса передаются отдельными элементами: пробелы и Unicode не искажаются.
        Assert.Equal(new[] { "run-due", "--home", home }, invocation.ChildArguments());
    }

    [Fact]
    public void Parse_IsCaseInsensitiveForCommandOnly()
    {
        Assert.True(LauncherArgs.TryParse(new[] { "RUN-DUE" }, out _, out _));
        Assert.False(LauncherArgs.TryParse(new[] { "run-due", "--HOME", "x" }, out _, out _));
    }

    [Fact]
    public void Parse_RejectsAnythingOutsideNarrowContract()
    {
        var rejected = new[]
        {
            Array.Empty<string>(),
            new[] { "schedule" },
            new[] { "run-due", "--text", "привет" },
            new[] { "run-due", "--home" },
            new[] { "run-due", "--home", "a", "--home", "b" },
            new[] { "run-due", "list" }
        };

        foreach (var args in rejected)
        {
            var ok = LauncherArgs.TryParse(args, out var invocation, out var error);
            Assert.False(ok, "принят вызов вне контракта: " + string.Join(" ", args));
            Assert.Null(invocation);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Fact]
    public void StartInfo_StartsConsoleChildWithoutAnyWindow()
    {
        var info = LauncherRunner.BuildStartInfo(
            @"C:\app\AgentNotify.exe",
            new[] { "run-due", "--home", @"C:\Users\Иван Петров\AppData\Local\AgentNotification" });

        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Equal(@"C:\app\AgentNotify.exe", info.FileName);
        Assert.Equal(@"C:\app", info.WorkingDirectory);
        Assert.Equal(new[] { "run-due", "--home", @"C:\Users\Иван Петров\AppData\Local\AgentNotification" }, info.ArgumentList);
    }

    [Fact]
    public void ResolveTarget_UsesSiblingAgentNotifyExeAndIgnoresPathArgument()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var launcher = Path.Combine(directory, "AgentNotify.Launcher.exe");
            File.WriteAllText(launcher, "stub");
            var target = Path.Combine(directory, "AgentNotify.exe");
            File.WriteAllText(target, "stub");

            Assert.Equal(target, LauncherRunner.ResolveTarget(launcher));
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void ResolveTarget_ReturnsNullWhenMainExeMissing()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var launcher = Path.Combine(directory, "AgentNotify.Launcher.exe");
            File.WriteAllText(launcher, "stub");
            Assert.Null(LauncherRunner.ResolveTarget(launcher));
            Assert.Null(LauncherRunner.ResolveTarget(null));
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }

    [Fact]
    public void Run_BadArgumentsReturnsDedicatedExitCode()
    {
        var exit = LauncherRunner.Run(new[] { "schedule", "--text", "x" }, null);
        Assert.Equal(LauncherRunner.ExitBadArguments, exit);
    }

    [Fact]
    public void Run_MissingTargetReturnsDedicatedExitCode()
    {
        var directory = TestFiles.NewDirectory();
        try
        {
            var launcher = Path.Combine(directory, "AgentNotify.Launcher.exe");
            File.WriteAllText(launcher, "stub");
            var exit = LauncherRunner.Run(new[] { "run-due", "--home", directory }, launcher);
            Assert.Equal(LauncherRunner.ExitMissingTarget, exit);
        }
        finally
        {
            TestFiles.Delete(directory);
        }
    }
}

/// <summary>Вспомогательные временные каталоги для файловых проверок лаунчера.</summary>
internal static class TestFiles
{
    public static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentnotify-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static void Delete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); } catch { /* временный каталог */ }
    }
}
