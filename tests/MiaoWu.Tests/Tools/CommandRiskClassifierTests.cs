using MiaoWu.Core.Agents.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class CommandRiskClassifierTests
    {
        [Theory]
        [InlineData("format C:")]
        [InlineData("FORMAT.EXE D: /q")]
        [InlineData(@"C:\Windows\System32\diskpart.exe")]
        [InlineData("diskpart")]
        [InlineData("bcdedit /set testsigning on")]
        [InlineData("shutdown /s /t 0")]
        [InlineData("logoff")]
        [InlineData(@"reg delete HKLM\Software\Foo /f")]
        [InlineData("net user bob /delete")]
        [InlineData("vssadmin delete shadows /all")]
        [InlineData("cipher /w:C")]
        [InlineData("schtasks /delete /tn foo /f")]
        [InlineData("sc delete MyService")]
        public void DangerousCommandsAreBlocked(string commandLine)
        {
            CommandRiskAssessment assessment = CommandRiskClassifier.Classify(commandLine);

            Assert.Equal(ToolRisk.Blocked, assessment.Risk);
            Assert.True(assessment.IsBlocked);
            Assert.False(string.IsNullOrWhiteSpace(assessment.Reason));
        }

        [Theory]
        [InlineData("curl https://example.com")]
        [InlineData("wget http://example.com/a.txt")]
        [InlineData("ssh user@host")]
        [InlineData("Invoke-WebRequest https://example.com")]
        [InlineData("iwr https://example.com -OutFile a.txt")]
        [InlineData("bitsadmin /transfer job https://example.com/a C:\\a")]
        [InlineData("ping example.com")]
        public void NetworkCommandsAreHighRisk(string commandLine)
        {
            Assert.Equal(ToolRisk.High, CommandRiskClassifier.Classify(commandLine).Risk);
        }

        [Theory]
        [InlineData("del important.txt")]
        [InlineData("rm -rf src")]
        [InlineData("Remove-Item .\\src -Recurse")]
        [InlineData("move a.txt b.txt")]
        [InlineData("taskkill /f /im notepad.exe")]
        [InlineData("sc stop Spooler")]
        [InlineData("reg add HKCU\\Software\\Foo /v Bar /d 1")]
        [InlineData("takeown /f C:\\Windows\\System32")]
        [InlineData("git reset --hard")]
        [InlineData("git clean -fd")]
        [InlineData("pip install requests")]
        public void MutatingCommandsAreHighRisk(string commandLine)
        {
            Assert.Equal(ToolRisk.High, CommandRiskClassifier.Classify(commandLine).Risk);
        }

        [Theory]
        [InlineData("dir")]
        [InlineData("Get-ChildItem -Force")]
        [InlineData("type README.md")]
        [InlineData("netstat -ano")]
        [InlineData("ipconfig /all")]
        [InlineData("whoami")]
        [InlineData("git status")]
        [InlineData("git diff HEAD~1")]
        [InlineData("git log --oneline -5")]
        [InlineData("findstr TODO src\\*.cs")]
        public void ReadOnlyCommandsAreLowRisk(string commandLine)
        {
            Assert.Equal(ToolRisk.Low, CommandRiskClassifier.Classify(commandLine).Risk);
        }

        [Theory]
        [InlineData("my-custom-tool.exe --help")]
        [InlineData("node scripts/build.js")]
        [InlineData("echo hello > out.txt")]
        [InlineData("echo hello < in.txt")]
        public void UnknownOrRedirectedCommandsAreMediumRisk(string commandLine)
        {
            Assert.Equal(ToolRisk.Medium, CommandRiskClassifier.Classify(commandLine).Risk);
        }

        [Fact]
        public void EmptyCommandIsMediumRisk()
        {
            Assert.Equal(ToolRisk.Medium, CommandRiskClassifier.Classify(null).Risk);
            Assert.Equal(ToolRisk.Medium, CommandRiskClassifier.Classify("   ").Risk);
        }

        [Fact]
        public void NestedPowerShellIsUnwrapped()
        {
            Assert.Equal(
                ToolRisk.High,
                CommandRiskClassifier.Classify("powershell -NoProfile -Command \"curl https://example.com\"").Risk);
            Assert.Equal(
                ToolRisk.Low,
                CommandRiskClassifier.Classify("cmd /c dir").Risk);
        }

        [Fact]
        public void EncodedCommandsAreHighRisk()
        {
            CommandRiskAssessment assessment = CommandRiskClassifier.Classify(
                "powershell -NoProfile -EncodedCommand ZgBvAG8A");

            Assert.Equal(ToolRisk.High, assessment.Risk);
            Assert.Equal("encoded_command", assessment.MatchedRule);
        }

        [Fact]
        public void CompoundCommandTakesWorstRisk()
        {
            Assert.Equal(
                ToolRisk.High,
                CommandRiskClassifier.Classify("dir && curl https://example.com").Risk);
            Assert.Equal(
                ToolRisk.Low,
                CommandRiskClassifier.Classify("dir && git status").Risk);
        }

        [Fact]
        public void BlockedRuleIsReported()
        {
            CommandRiskAssessment assessment = CommandRiskClassifier.Classify("format C:");

            Assert.Equal("format", assessment.MatchedRule);
            Assert.Contains("format", assessment.Reason);
        }
    }
}
