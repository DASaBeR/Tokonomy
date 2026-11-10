namespace Tokonomy.Runtime.Gates.Isolation;

/// <summary>
/// The PowerShell script that runs <b>inside</b> the sandbox (Windows PowerShell 5.1, which every Windows image has). It
/// lives in the read-only control folder, so code under test cannot rewrite it for later commands. It has four modes:
/// <list type="bullet">
/// <item><c>probe</c>: exits 0 (readiness check).</item>
/// <item><c>setup</c>: unpacks the dependency archive, then copies the read-only source snapshot to the sandbox's own disk (<c>C:\work</c>).</item>
/// <item><c>run</c>: runs <c>ctl\cmd-N.json</c> with a fixed environment, enforces its timeout with a tree kill, and writes
/// <c>out\N.log</c> (tail-capped), <c>out\N.report</c> (if declared, a plain file, size-capped) and <c>out\N.status.json</c>.</item>
/// <item><c>export</c>: archives what prepare produced (files that are not in the snapshot, plus package caches) to <c>out\deps.tar</c>.</item>
/// </list>
/// Nothing it writes is trusted by the host: the host validates every file it takes from <c>out</c>.
/// </summary>
internal static class SandboxRunnerScript
{
    public const string FileName = "run.ps1";
    public const string SandboxControl = @"C:\tk\ctl";
    public const string SandboxSource = @"C:\tk\src";
    public const string SandboxDependencies = @"C:\tk\deps";
    public const string SandboxOutput = @"C:\tk\out";
    public const string DependencyArchive = "deps.tar";

    /// <summary>Longest spec accepted; keeps the whole exec command line well under Windows' 32,767-character limit.</summary>
    public const int MaxSpecChars = 24_000;

    public static string CommandLine(string mode, int index = -1, string? spec = null) =>
        $@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {SandboxControl}\{FileName} -Mode {mode}" +
        (index >= 0 ? $" -Index {index}" : "") +
        (spec is null ? "" : $" -Spec {spec}");

    /// <summary>The command spec as base64 UTF-8 JSON (only <c>[A-Za-z0-9+/=]</c>, so it needs no quoting on any command line).</summary>
    public static string EncodeSpec(GateCommand command, Tokonomy.Core.Config.GateArtifactLimits limits)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(limits);
        var args = command.Args;
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            name = command.Name,
            exe = args[0],
            args_line = Tokonomy.Runtime.Process.WindowsCommandLine.Join(args.Skip(1)),
            cmd_args = string.Concat(args.Skip(1).Select(a => " " + Tokonomy.Runtime.Process.ExecutableResolver.EscapeArgument(a, false))),
            cmd_args_shim = string.Concat(args.Skip(1).Select(a => " " + Tokonomy.Runtime.Process.ExecutableResolver.EscapeArgument(a, true))),
            timeout_ms = (long)command.Timeout.TotalMilliseconds,
            report = command.ReportPath?.Replace('/', '\\'),
            max_log_bytes = limits.MaxLogKb * 1024L,
            max_report_bytes = limits.MaxReportMb * 1024L * 1024,
        });
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        return encoded.Length <= MaxSpecChars
            ? encoded
            : throw new GateEnvironmentException($"gate '{command.Name}': its command line is too long to pass into the sandbox ({encoded.Length} encoded characters, limit {MaxSpecChars})");
    }

    public const string Text = """
        param(
          [Parameter(Mandatory = $true)][ValidateSet('probe', 'setup', 'run', 'export')][string]$Mode,
          [int]$Index = -1,
          [string]$Spec = '',
          [string]$Root = 'C:\'
        )
        $ErrorActionPreference = 'Continue'
        $ProgressPreference = 'SilentlyContinue'
        $Tk = Join-Path $Root 'tk'; $Out = Join-Path $Tk 'out'; $Work = Join-Path $Root 'work'
        $Cache = Join-Path $Root 'tkc'; $HomeDir = Join-Path $Root 'tkh'; $Scratch = Join-Path $Root 'tks'
        $Sys = Join-Path $env:SystemRoot 'System32'
        if ($Mode -eq 'probe') { exit 0 }

        function Write-Text([string]$Path, [string]$Text) {
          [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
        }

        function Read-Tail([string]$Path, [long]$Max) {
          if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
          $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
          try {
            $len = $fs.Length
            $start = [Math]::Max([long]0, $len - $Max)
            [void]$fs.Seek($start, 'Begin')
            $buf = New-Object byte[] ([int]($len - $start))
            $read = 0
            while ($read -lt $buf.Length) {
              $n = $fs.Read($buf, $read, $buf.Length - $read)
              if ($n -le 0) { break }
              $read += $n
            }
            $text = [Text.Encoding]::UTF8.GetString($buf, 0, $read)
            if ($start -gt 0) { $text = "...[output truncated]`n" + $text }
            return $text
          } finally { $fs.Dispose() }
        }

        function Set-GateEnvironment($Session) {
          # Start from an allowlist: nothing the runner inherited reaches the command except basic OS facts.
          $keep = @('SystemRoot', 'windir', 'ComSpec', 'PATHEXT', 'SystemDrive', 'OS', 'NUMBER_OF_PROCESSORS',
                    'PROCESSOR_ARCHITECTURE', 'PROCESSOR_IDENTIFIER', 'PROCESSOR_LEVEL', 'PROCESSOR_REVISION',
                    'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432', 'CommonProgramFiles', 'CommonProgramFiles(x86)',
                    'CommonProgramW6432', 'ProgramData', 'ALLUSERSPROFILE', 'PUBLIC', 'COMPUTERNAME', 'PSModulePath')
          Get-ChildItem env: | Where-Object { $keep -notcontains $_.Name } | ForEach-Object { Remove-Item -LiteralPath ('env:' + $_.Name) }
          foreach ($d in @($Work, $Cache, $HomeDir, $Scratch, "$HomeDir\AppData\Roaming", "$HomeDir\AppData\Local")) {
            [void](New-Item -ItemType Directory -Force -Path $d)
          }
          $paths = New-Object System.Collections.Generic.List[string]
          foreach ($t in @($Session.toolchains)) {
            if (-not $t) { continue }
            foreach ($sub in @('', '\bin', '\cmd', '\Scripts')) {
              $p = [string]$t + $sub
              if (Test-Path -LiteralPath $p -PathType Container) { $paths.Add($p) }
            }
            if (Test-Path -LiteralPath "$t\dotnet.exe" -PathType Leaf) { $env:DOTNET_ROOT = [string]$t }
          }
          foreach ($p in @($Sys, $env:SystemRoot, "$Sys\Wbem", "$Sys\WindowsPowerShell\v1.0")) { $paths.Add($p) }
          $env:PATH = ($paths -join ';')
          $env:HOME = $HomeDir; $env:USERPROFILE = $HomeDir
          $env:APPDATA = "$HomeDir\AppData\Roaming"; $env:LOCALAPPDATA = "$HomeDir\AppData\Local"
          $env:TEMP = $Scratch; $env:TMP = $Scratch
          $env:NUGET_PACKAGES = "$Cache\nuget"; $env:DOTNET_CLI_HOME = $HomeDir
          $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
          $env:MSBUILDDISABLENODEREUSE = '1'; $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
          $env:npm_config_cache = "$Cache\npm"; $env:npm_config_update_notifier = 'false'
          $env:PIP_CACHE_DIR = "$Cache\pip"; $env:PIP_DISABLE_PIP_VERSION_CHECK = '1'; $env:PYTHONUTF8 = '1'
          $env:CI = 'true'
        }

        try {
          $Session = Get-Content -LiteralPath "$Tk\ctl\session.json" -Raw | ConvertFrom-Json
          Set-GateEnvironment $Session

          if ($Mode -eq 'setup') {
            $log = "$Out\setup.log"
            if (Test-Path -LiteralPath "$Tk\deps\deps.tar" -PathType Leaf) {
              & "$Sys\tar.exe" -xf "$Tk\deps\deps.tar" -C $Root *>> $log
              if ($LASTEXITCODE -ne 0) { exit 70 }
            }
            & "$Sys\robocopy.exe" "$Tk\src" $Work /E /COPY:DAT /DCOPY:DAT /R:0 /W:0 /NFL /NDL /NJH /NJS /NP *>> $log
            if ($LASTEXITCODE -ge 8) { exit 71 }
            exit 0
          }

          if ($Mode -eq 'run') {
            if (-not $Spec) { throw 'no -Spec given' }
            $job = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Spec)) | ConvertFrom-Json
            if (-not $job -or -not $job.exe) { throw 'the command spec is empty or unreadable' }
            $name = [string]$Index
            $stdout = "$Scratch\$Index.out"; $stderr = "$Scratch\$Index.err"; $empty = "$Scratch\empty.txt"
            Write-Text $empty ''
            $exe = [string]$job.exe
            $resolved = $null
            if ($exe -match '[\\/]') {
              $candidate = if ([IO.Path]::IsPathRooted($exe)) { $exe } else { Join-Path $Work $exe }
              foreach ($ext in @('', '.exe', '.cmd', '.bat', '.com')) {
                if (Test-Path -LiteralPath ($candidate + $ext) -PathType Leaf) { $resolved = $candidate + $ext; break }
              }
            } else {
              $found = Get-Command -Name $exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
              if ($found) { $resolved = $found.Source }
            }

            if (-not $resolved) {
              Write-Text "$Out\$name.log" ("[tokonomy] '" + $exe + "' was not found inside the gate environment. PATH: " + $env:PATH)
              Write-Text "$Out\$name.status.json" (ConvertTo-Json @{ nonce = $Session.nonce; index = $Index; exit = 127; timed_out = $false; not_found = $true } -Compress)
              exit 127
            }

            if ($resolved -match '\.(cmd|bat)$') {
              $file = "$Sys\cmd.exe"
              $escaped = $resolved -replace '([()\][%!^"`<>&|;, *?])', '^$1'
              $rest = if ($resolved -match 'node_modules[\\/]\.bin[\\/][^\\/]+\.cmd$') { [string]$job.cmd_args_shim } else { [string]$job.cmd_args }
              $argLine = '/d /s /c "' + $escaped + $rest + '"'
            } else {
              $file = $resolved
              $argLine = [string]$job.args_line
            }

            $sp = @{ FilePath = $file; WorkingDirectory = $Work; NoNewWindow = $true; PassThru = $true
                     RedirectStandardOutput = $stdout; RedirectStandardError = $stderr; RedirectStandardInput = $empty }
            if ($argLine.Length -gt 0) { $sp['ArgumentList'] = $argLine }
            $p = Start-Process @sp
            $null = $p.Handle
            $timedOut = -not $p.WaitForExit([int]$job.timeout_ms)
            if ($timedOut) {
              & "$Sys\taskkill.exe" /T /F /PID $p.Id *> $null
              $code = -1
            } else {
              $p.WaitForExit()
              $code = $p.ExitCode
            }

            $max = [long]$job.max_log_bytes
            $text = (Read-Tail $stdout $max) + "`n" + (Read-Tail $stderr ([long][Math]::Max(4096, $max / 4)))
            if ($timedOut) { $text += "`n[tokonomy] killed after " + ([int]$job.timeout_ms / 1000) + "s" }
            Write-Text "$Out\$name.log" $text

            if ($job.report) {
              $rp = Join-Path $Work ([string]$job.report)
              $item = Get-Item -LiteralPath $rp -Force -ErrorAction SilentlyContinue
              if ($item -and -not $item.PSIsContainer -and -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -and $item.Length -le [long]$job.max_report_bytes) {
                Copy-Item -LiteralPath $rp -Destination "$Out\$name.report" -Force
              }
            }

            Write-Text "$Out\$name.status.json" (ConvertTo-Json @{ nonce = $Session.nonce; index = $Index; exit = $code; timed_out = $timedOut; not_found = $false } -Compress)
            if ($timedOut) { exit 124 }
            exit $code
          }

          if ($Mode -eq 'export') {
            $src = "$Tk\src"
            $known = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
            Get-ChildItem -LiteralPath $src -Recurse -Force -File -ErrorAction SilentlyContinue |
              ForEach-Object { [void]$known.Add($_.FullName.Substring($src.Length + 1)) }
            $entries = New-Object System.Collections.Generic.List[string]
            Get-ChildItem -LiteralPath $Work -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object {
              $rel = $_.FullName.Substring($Work.Length + 1)
              if (-not $known.Contains($rel)) { $entries.Add('work/' + $rel.Replace('\', '/')) }
            }
            if ((Get-ChildItem -LiteralPath $Cache -Force -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0) { $entries.Add('tkc') }
            $list = "$Scratch\deps.list"
            [IO.File]::WriteAllLines($list, [string[]]$entries, (New-Object Text.UTF8Encoding($false)))
            Push-Location $Root
            & "$Sys\tar.exe" -cf "$Out\deps.tar" -C $Root -T $list *> "$Out\export.log"
            $code = $LASTEXITCODE
            Pop-Location
            exit $code
          }
        } catch {
          Write-Text "$Out\runner-error.log" ($Mode + ': ' + $_.ToString() + "`n" + $_.ScriptStackTrace)
          exit 90
        }
        """;
}
