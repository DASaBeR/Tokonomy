using System.Text.Json;
using Tokonomy.Core.Config;
using Tokonomy.Core.Model;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Tests;

/// <summary>Fixtures mirror real <c>agy 1.5.3 --output-format stream-json</c> output captured while probing its permission behaviour.</summary>
internal static class AgyFixtures
{
    public const string Model = "gemini-3.8-flash-medium";
    public const string Usage = """{"input_tokens":14453,"output_tokens":448,"thinking_tokens":314,"cache_read_tokens":0,"total_tokens":14901}""";

    public static string Init(string model = Model) =>
        "{\"event\":\"init\",\"conversation_id\":\"c1\",\"init\":{\"model\":\"" + model +
        "\",\"cwd\":\"X\",\"tools\":[\"read_file\",\"run_command\"],\"permission_mode\":\"request-review\"}}";

    public static string Tool(string name, string state, string parameters, int step = 2) =>
        "{\"event\":\"step_update\",\"step_update\":{\"conversation_id\":\"c1\",\"step_index\":" + step +
        ",\"state\":\"" + state + "\",\"step_type\":\"tool\",\"tool_name\":\"" + name +
        "\",\"tool_info\":{\"name\":\"" + name + "\",\"parameters\":" + parameters + "}}}";

    public static string Result(string status = "SUCCESS", string response = "done\n", string extra = "") =>
        "{\"event\":\"result\",\"result\":{\"conversation_id\":\"c1\",\"status\":\"" + status +
        "\",\"response\":" + JsonSerializer.Serialize(response) +
        ",\"duration_seconds\":7.9,\"num_turns\":1,\"usage\":" + Usage + extra + "}}";

    public static string Stream(params string[] lines) => string.Join('\n', lines) + "\n";

    public static string Path(string workdir, string file) => JsonSerializer.Serialize(new { Path = System.IO.Path.Combine(workdir, file) });

    public static string Cmd(string command) => JsonSerializer.Serialize(new { CommandLine = command });

    /// <summary>A clean run: read, edit, finish.</summary>
    public static string CleanEdit(string workdir) => Stream(
        Init(),
        Tool("read_file", "DONE", Path(workdir, "calc.py"), 2),
        Tool("edit_file", "ACTIVE", Path(workdir, "calc.py"), 4),
        Tool("edit_file", "DONE", Path(workdir, "calc.py"), 4),
        Result());

    /// <summary>Headless auto-deny: no rule matched, the session ended at the first command. Exit 0, SUCCESS, empty response.</summary>
    public static string HeadlessDenied(string command) => Stream(
        Init(),
        Tool("run_command", "ACTIVE", Cmd(command), 4),
        Tool("run_command", "DONE", Cmd(command), 4),
        Result(response: "", extra: ",\"denied_actions\":[{\"action\":\"command\",\"display_name\":\"RunCommand\"}]"));

    /// <summary>An explicit deny rule: SUCCESS, NO denied_actions, the step ends in ERROR and the model explains it.</summary>
    public static string ExplicitDeny(string command) => Stream(
        Init(),
        Tool("run_command", "ACTIVE", Cmd(command), 4),
        Tool("run_command", "ERROR", Cmd(command), 4),
        Result(response: "The command could not be executed because it was blocked by a user-configured permission rule:\n> **Permission denied**: `permission check failed for unsandboxed \"" + command + "\": Matches user-configured deny rule.`\n"));
}

public class AgyRunParserTests
{
    private const string Wd = "wd";

    [Fact]
    public void Reads_model_usage_status_and_tool_calls_from_a_stream()
    {
        var report = AgyRunParser.Parse(AgyFixtures.CleanEdit(Wd), "ignored");

        Assert.True(report.HasResult);
        Assert.Equal(AgyFixtures.Model, report.Model);
        Assert.Equal("SUCCESS", report.Status);
        Assert.Equal("c1", report.ConversationId);
        Assert.Equal("done\n", report.Response);
        var usage = Assert.Single(report.Usage);
        Assert.Equal(AgyFixtures.Model, usage.Model);        // labelled with the model the session reported
        Assert.Equal(14453, usage.InputTokens);
        Assert.Equal(448 + 314, usage.OutputTokens);        // thinking counts as output
        Assert.Equal(["read_file", "edit_file"], report.Tools.Select(t => t.Name));
        Assert.All(report.Tools, t => Assert.Equal("DONE", t.State));   // ACTIVE then DONE is one call, final state kept
        Assert.Empty(report.DeniedActions);
    }

    [Fact]
    public void Reads_headless_denied_actions_and_the_command_that_was_refused()
    {
        var report = AgyRunParser.Parse(AgyFixtures.HeadlessDenied("python -c \"print(1)\""), null);

        Assert.Equal(["command: RunCommand"], report.DeniedActions);
        var call = Assert.Single(report.Tools);
        Assert.True(call.IsCommand);
        Assert.Equal("python -c \"print(1)\"", call.Target);
    }

    [Fact]
    public void An_explicit_deny_shows_up_as_an_errored_tool_step()
    {
        var report = AgyRunParser.Parse(AgyFixtures.ExplicitDeny("python -m unittest -q"), null);

        Assert.Empty(report.DeniedActions);                 // agy does NOT list rule denials here
        Assert.True(Assert.Single(report.Tools).Errored);
    }

    [Fact]
    public void Reads_plain_json_output_and_an_error_status()
    {
        const string json = """{"conversation_id":"","status":"ERROR","response":"","error":"invalid model selection","duration_seconds":0,"num_turns":0,"usage":{"input_tokens":0,"output_tokens":0,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":0},"finished_reason":"error"}""";

        var report = AgyRunParser.Parse(json, "m");

        Assert.True(report.HasResult);
        Assert.Equal("ERROR", report.Status);
        Assert.Equal("invalid model selection", report.Error);
        Assert.Null(report.Model);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text, not json")]
    [InlineData("{\"status\":\"SUCCESS\"")]
    public void Garbage_has_no_result(string stdout) => Assert.False(AgyRunParser.Parse(stdout).HasResult);

    [Fact]
    public void A_stream_cut_off_before_the_result_has_no_result()
    {
        var cut = AgyFixtures.Stream(AgyFixtures.Init(), AgyFixtures.Tool("read_file", "ACTIVE", AgyFixtures.Path(Wd, "a")));
        var report = AgyRunParser.Parse(cut);
        Assert.False(report.HasResult);
        Assert.Equal(AgyFixtures.Model, report.Model);
    }
}

public class AgyRunInterpreterTests
{
    private static readonly string Workdir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tokonomy-agy-wd"));
    private static readonly string[] Allowed = ["python -m unittest"];

    private static WorkerFault? Eval(string stdout, int exit = 0, string stderr = "", string model = AgyFixtures.Model, bool verify = true, string[]? allowed = null) =>
        AgyRunInterpreter.Evaluate(AgyRunParser.Parse(stdout, model), exit, stderr, model, verify, Workdir, allowed ?? Allowed);

    [Fact]
    public void A_clean_edit_has_no_fault() => Assert.Null(Eval(AgyFixtures.CleanEdit(Workdir)));

    [Fact]
    public void Running_an_allowed_command_is_fine()
    {
        var stdout = AgyFixtures.Stream(
            AgyFixtures.Init(),
            AgyFixtures.Tool("run_command", "DONE", AgyFixtures.Cmd("python -m unittest")),
            AgyFixtures.Result());
        Assert.Null(Eval(stdout));
    }

    [Fact]
    public void Headless_auto_deny_is_a_fault_even_though_exit_is_0_and_status_is_success()
    {
        var fault = Eval(AgyFixtures.HeadlessDenied("python -c \"print(1)\""));

        Assert.Equal(WorkerFaultKind.PermissionDenied, fault?.Kind);
        Assert.Contains("python -c", fault!.Detail, StringComparison.Ordinal);
        Assert.Contains("python -m unittest", fault.Detail, StringComparison.Ordinal);   // tells the retry what IS allowed
    }

    [Fact]
    public void An_explicit_deny_rule_is_detected_although_denied_actions_is_empty()
    {
        // The command is in the allowlist (an operator also denied it), so only the ERROR step and the prose reveal it.
        var fault = Eval(AgyFixtures.ExplicitDeny("python -m unittest"));
        Assert.Equal(WorkerFaultKind.PermissionDenied, fault?.Kind);
    }

    [Fact]
    public void A_command_outside_the_allowlist_is_a_fault_even_if_it_somehow_ran()
    {
        var stdout = AgyFixtures.Stream(
            AgyFixtures.Init(),
            AgyFixtures.Tool("run_command", "DONE", AgyFixtures.Cmd("Get-ChildItem")),
            AgyFixtures.Result());

        Assert.Equal(WorkerFaultKind.PermissionDenied, Eval(stdout)?.Kind);
    }

    [Fact]
    public void An_allowed_command_run_in_another_directory_is_a_fault()
    {
        var outside = Path.Combine(Path.GetTempPath(), "someone-elses-project");
        string Run(string cwd) => AgyFixtures.Stream(
            AgyFixtures.Init(),
            AgyFixtures.Tool("run_command", "DONE", JsonSerializer.Serialize(new { CommandLine = "python -m unittest", Cwd = cwd })),
            AgyFixtures.Result());

        Assert.Equal(WorkerFaultKind.PermissionDenied, Eval(Run(outside))?.Kind);
        Assert.Null(Eval(Run(Workdir)));
        Assert.Null(Eval(Run(Path.Combine(Workdir, "sub"))));
    }

    [Theory]
    [InlineData("python -m \"unittest\"")]           // quoting
    [InlineData("python -m 'unittest'")]
    [InlineData("& python -m unittest")]             // call operator
    [InlineData("python.exe -m unittest")]           // alternate spelling of the executable
    [InlineData("C:\\evil\\python -m unittest")]     // alternate executable
    [InlineData("cmd /c python -m unittest")]        // another shell
    [InlineData("python -m unittest $(whoami)")]     // substitution
    [InlineData("python -m unittest `whoami`")]
    [InlineData("python -m unittest | Out-File x")]  // pipe
    [InlineData("python -m unittest && whoami")]     // chaining
    [InlineData("python -m unittest\nwhoami")]       // newline
    [InlineData("cd .. ; python -m unittest")]       // working directory change
    public void Manipulated_forms_of_an_allowed_command_are_faults(string attempt)
    {
        var stdout = AgyFixtures.Stream(AgyFixtures.Init(), AgyFixtures.Tool("run_command", "DONE", AgyFixtures.Cmd(attempt)), AgyFixtures.Result());
        Assert.Equal(WorkerFaultKind.PermissionDenied, Eval(stdout)?.Kind);
    }

    [Fact]
    public void Command_matching_ignores_case_and_extra_whitespace_only()
    {
        string Run(string cmd) => AgyFixtures.Stream(AgyFixtures.Init(), AgyFixtures.Tool("run_command", "DONE", AgyFixtures.Cmd(cmd)), AgyFixtures.Result());

        Assert.Null(Eval(Run("python   -m  unittest")));
        Assert.NotNull(Eval(Run("python -m unittest -q")));            // an extra argument is a different command
        Assert.NotNull(Eval(Run("python -m unittest; echo hi")));      // chaining is a different command
    }

    [Fact]
    public void With_no_allowlist_any_command_is_a_fault_and_the_message_says_so()
    {
        var fault = Eval(AgyFixtures.HeadlessDenied("git status"), allowed: []);
        Assert.Contains("no shell commands are permitted", fault!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_model_is_a_model_mismatch()
    {
        var stdout = AgyFixtures.CleanEdit(Workdir).Replace(AgyFixtures.Model, "claude-opus-4-6-thinking", StringComparison.Ordinal);
        var fault = Eval(stdout);
        Assert.Equal(WorkerFaultKind.ModelMismatch, fault?.Kind);
        Assert.Contains("claude-opus-4-6-thinking", fault!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_mismatch_wins_over_every_other_fault()
    {
        var stdout = AgyFixtures.HeadlessDenied("x").Replace(AgyFixtures.Model, "gpt-oss-120b-medium", StringComparison.Ordinal);
        Assert.Equal(WorkerFaultKind.ModelMismatch, Eval(stdout)?.Kind);
    }

    [Fact]
    public void An_unreported_model_fails_closed_when_verification_is_on()
    {
        const string json = """{"conversation_id":"c","status":"SUCCESS","response":"ok","usage":{"input_tokens":1,"output_tokens":1}}""";
        Assert.Equal(WorkerFaultKind.ModelMismatch, Eval(json)?.Kind);
        Assert.Null(Eval(json, verify: false));
    }

    [Fact]
    public void Model_ids_are_compared_exactly_not_by_prefix()
    {
        Assert.True(AgyRunInterpreter.ModelsMatch("gemini-3.8-flash-medium", "Gemini-3.8-Flash-Medium"));
        Assert.False(AgyRunInterpreter.ModelsMatch("gemini-3.8-flash-medium", "gemini-3.8-flash-high"));
        Assert.False(AgyRunInterpreter.ModelsMatch("gemini-3.8-flash", "gemini-3.8-flash-medium"));
    }

    [Fact]
    public void An_unknown_model_is_a_permanent_model_fault_with_agys_own_message_not_a_retryable_error()
    {
        // Captured from `agy -p ... --model not-a-real-model --output-format json` (exit 1).
        const string json = """{"conversation_id":"","status":"ERROR","response":"","error":"invalid model selection (--model \"not-a-real-model\" --effort \"\"): model not-a-real-model is not recognized as a known model or custom model in settings","duration_seconds":0,"num_turns":0,"usage":{"input_tokens":0,"output_tokens":0,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":0},"finished_reason":"error"}""";

        var fault = Eval(json, exit: 1, model: "not-a-real-model");

        Assert.Equal(WorkerFaultKind.ModelMismatch, fault?.Kind);
        Assert.Contains("not recognized as a known model", fault!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_errors_without_a_model_are_reported_as_errors_not_model_faults()
    {
        const string json = """{"conversation_id":"","status":"ERROR","response":"","error":"quota exceeded","usage":{"input_tokens":0,"output_tokens":0}}""";

        Assert.Equal(WorkerFaultKind.ReportedError, Eval(json, exit: 1)?.Kind);
    }

    [Fact]
    public void An_error_status_is_a_fault_even_with_exit_code_0()
    {
        var stdout = AgyFixtures.Stream(AgyFixtures.Init(), AgyFixtures.Result(status: "ERROR", response: "", extra: ""));
        var fault = Eval(stdout);
        Assert.Equal(WorkerFaultKind.ReportedError, fault?.Kind);
    }

    [Fact]
    public void A_non_zero_exit_is_a_fault_even_when_the_result_says_success()
    {
        var fault = Eval(AgyFixtures.CleanEdit(Workdir), exit: 3, stderr: "boom");
        Assert.Equal(WorkerFaultKind.ProcessFailed, fault?.Kind);
        Assert.Contains("boom", fault!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void No_output_at_all_is_a_fault_not_a_success()
    {
        Assert.Equal(WorkerFaultKind.NoResult, Eval("", exit: 0)?.Kind);
        Assert.Equal(WorkerFaultKind.ProcessFailed, Eval("", exit: 1, stderr: "crash")?.Kind);
    }

    [Fact]
    public void A_refused_file_tool_outside_the_worktree_is_a_fault_but_an_ordinary_in_tree_error_is_not()
    {
        var outside = AgyFixtures.Stream(
            AgyFixtures.Init(),
            AgyFixtures.Tool("write_file", "ERROR", JsonSerializer.Serialize(new { Path = Path.Combine(Path.GetTempPath(), "elsewhere", "x.txt") })),
            AgyFixtures.Result());
        Assert.Equal(WorkerFaultKind.PermissionDenied, Eval(outside)?.Kind);

        var inTree = AgyFixtures.Stream(
            AgyFixtures.Init(),
            AgyFixtures.Tool("edit_file", "ERROR", AgyFixtures.Path(Workdir, "calc.py"), 4),   // e.g. the search text did not match
            AgyFixtures.Tool("edit_file", "DONE", AgyFixtures.Path(Workdir, "calc.py"), 6),
            AgyFixtures.Result());
        Assert.Null(Eval(inTree));
    }

    [Fact]
    public void A_file_tool_refused_by_one_of_our_deny_rules_inside_the_worktree_is_a_fault()
    {
        // Captured live: the model tried .git/hooks/post-checkout, the deny rule refused it (ERROR step, no
        // denied_actions) and the model's explanation did not use any of agy's refusal phrases.
        var hook = Path.Combine(Workdir, ".git", "hooks", "post-checkout");
        var stdout = AgyFixtures.Stream(
            AgyFixtures.Init(),
            AgyFixtures.Tool("write_file", "ERROR", JsonSerializer.Serialize(new { Path = hook.Replace('\\', '/') })),
            AgyFixtures.Result(response: "I could not create that file."));

        var fault = AgyRunInterpreter.Evaluate(AgyRunParser.Parse(stdout), 0, "", AgyFixtures.Model, true, Workdir, Allowed,
            deniedPaths: [Path.Combine(Workdir, ".git")]);

        Assert.Equal(WorkerFaultKind.PermissionDenied, fault?.Kind);
        Assert.Null(AgyRunInterpreter.Evaluate(AgyRunParser.Parse(stdout), 0, "", AgyFixtures.Model, true, Workdir, Allowed,
            deniedPaths: [Path.Combine(Workdir, "elsewhere")]));   // an unrelated in-tree tool error stays an ordinary failure
    }

    [Fact]
    public void Settings_expose_the_paths_their_deny_rules_name()
    {
        var wt = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wt-denied"));
        var file = AgySettings.Build(wt, new ExecutorPermissions(), [], Path.Combine(Path.GetTempPath(), "nope"), homeDirectory: "/home/u");
        Assert.Contains(Path.Combine(wt, ".git"), file.DeniedPaths);
        Assert.Contains(Path.Combine("/home/u", ".gemini"), file.DeniedPaths);
    }

    [Fact]
    public void A_summary_that_merely_mentions_permission_denied_is_not_a_fault()
    {
        var stdout = AgyFixtures.Stream(AgyFixtures.Init(), AgyFixtures.Result(response: "Handled the Permission denied error in login()."));
        Assert.Null(Eval(stdout));
    }

    [Fact]
    public void Inside_is_a_directory_boundary_check_not_a_string_prefix()
    {
        Assert.True(AgyRunInterpreter.Inside(Workdir, Path.Combine(Workdir, "a", "b.txt")));
        Assert.True(AgyRunInterpreter.Inside(Workdir, "a.txt"));                         // relative = inside
        Assert.False(AgyRunInterpreter.Inside(Workdir, Workdir + "-other" + Path.DirectorySeparatorChar + "a.txt"));
        Assert.False(AgyRunInterpreter.Inside(Workdir, Path.Combine(Workdir, "..", "a.txt")));
    }
}

public class AgySettingsTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tokonomy-settings-test"));
    private static readonly string Worktree = Path.Combine(Root, "wt", "run1");
    private static readonly string Temp = Path.Combine(Root, "tmp");

    private static (List<string> Allow, List<string> Deny) Rules(AgySettingsFile file)
    {
        using var doc = JsonDocument.Parse(file.Json);
        var p = doc.RootElement.GetProperty("permissions");
        return (p.GetProperty("allow").EnumerateArray().Select(e => e.GetString()!).ToList(),
            p.GetProperty("deny").EnumerateArray().Select(e => e.GetString()!).ToList());
    }

    [Fact]
    public void Grants_file_access_to_the_worktree_only_and_one_literal_rule_per_gate_command()
    {
        var file = AgySettings.Build(Worktree, new ExecutorPermissions { AllowGateCommands = true }, ["python -m unittest", "ruff check ."], Temp);
        var (allow, _) = Rules(file);

        Assert.Equal([$"read_file({Worktree})", $"write_file({Worktree})", "command(python -m unittest)", "command(ruff check .)"], allow);
        Assert.Equal(["python -m unittest", "ruff check ."], file.AllowedCommands);
    }

    [Fact]
    public void Never_emits_a_wildcard_or_a_blanket_rule()
    {
        var file = AgySettings.Build(Worktree, new ExecutorPermissions { Commands = ["git status"], AllowGateCommands = true }, ["python -m unittest"], Temp);
        Assert.DoesNotContain("command(*)", file.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("*", file.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("dangerously", file.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void Denies_writes_to_the_temp_directory_when_the_worktree_is_outside_it()
    {
        var (_, deny) = Rules(AgySettings.Build(Worktree, new ExecutorPermissions(), [], Temp));
        Assert.Contains($"write_file({Temp})", deny);
        Assert.Contains($"read_file({Temp})", deny);
    }

    [Fact]
    public void Temp_read_and_write_denies_are_independent_switches()
    {
        static List<string> TempRules(List<string> deny) => deny.Where(d => d.EndsWith($"({Temp})", StringComparison.Ordinal)).ToList();

        var (_, noWrite) = Rules(AgySettings.Build(Worktree, new ExecutorPermissions { DenyTempWrites = false }, [], Temp));
        Assert.Equal([$"read_file({Temp})"], TempRules(noWrite));

        var (_, none) = Rules(AgySettings.Build(Worktree, new ExecutorPermissions { DenyTempWrites = false, DenyTempReads = false }, [], Temp));
        Assert.Empty(TempRules(none));
    }

    [Fact]
    public void Does_not_deny_temp_when_the_worktree_is_inside_it_because_deny_would_also_block_the_worktree()
    {
        var file = AgySettings.Build(Path.Combine(Temp, "wt"), new ExecutorPermissions(), [], Temp);
        var (_, deny) = Rules(file);

        Assert.DoesNotContain(deny, d => d.EndsWith($"({Temp})", StringComparison.Ordinal));
        Assert.Contains("worktree_root", Assert.Single(file.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public void The_git_pointer_and_tokonomy_state_are_never_writable_and_credential_folders_never_readable()
    {
        var home = Path.Combine(Root, "home");
        var (allow, deny) = Rules(AgySettings.Build(Worktree, new ExecutorPermissions(), [], Temp, homeDirectory: home));

        Assert.Contains($"write_file({Path.Combine(Worktree, ".git")})", deny);
        Assert.Contains($"write_file({Path.Combine(Worktree, ".tokonomy")})", deny);
        foreach (var name in new[] { ".gemini", ".claude", ".ssh", ".aws", ".git-credentials" })
        {
            Assert.Contains($"read_file({Path.Combine(home, name)})", deny);
            Assert.Contains($"write_file({Path.Combine(home, name)})", deny);
        }

        Assert.DoesNotContain(allow, a => a.Contains(home, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Restrictions_become_deny_rules_reads_only_where_asked()
    {
        var file = AgySettings.Build(Worktree, new ExecutorPermissions(), [], Temp, restrictions:
        [
            new PathRestriction("node_modules/evil", DenyRead: true, "link"),
            new PathRestriction("build/", DenyRead: false, "ignored"),
        ]);
        var (_, deny) = Rules(file);

        var evil = Path.Combine(Worktree, "node_modules", "evil");
        var build = Path.Combine(Worktree, "build");
        Assert.Contains($"write_file({evil})", deny);
        Assert.Contains($"read_file({evil})", deny);
        Assert.Contains($"write_file({build})", deny);
        Assert.DoesNotContain($"read_file({build})", deny);
    }

    [Fact]
    public void Gate_commands_are_not_granted_by_default_because_they_run_worker_written_code()
    {
        var file = AgySettings.Build(Worktree, new ExecutorPermissions(), ["python -m unittest"], Temp);
        Assert.Empty(file.AllowedCommands);
        Assert.DoesNotContain("command(", file.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_commands_are_optional_and_extra_commands_are_literal()
    {
        var file = AgySettings.Build(Worktree, new ExecutorPermissions { AllowGateCommands = false, Commands = ["git  diff"] }, ["python -m unittest"], Temp);
        Assert.Equal(["git diff"], file.AllowedCommands);   // whitespace normalised; the gate command was not granted
    }

    [Fact]
    public void A_command_with_parentheses_is_skipped_with_a_note_because_a_rule_cannot_express_it()
    {
        var file = AgySettings.Build(Worktree, new ExecutorPermissions { AllowGateCommands = true }, ["powershell -Command (Get-Date)"], Temp);
        Assert.Empty(file.AllowedCommands);
        Assert.Contains(file.Notes, n => n.Contains("parentheses", StringComparison.Ordinal));
    }

    [Fact]
    public void Raw_allow_and_deny_rules_pass_through_and_command_rules_join_the_allowlist()
    {
        var file = AgySettings.Build(Worktree, new ExecutorPermissions { Allow = ["read_url(docs.python.org)", "command(git status)"], Deny = ["command(git push)"] }, [], Temp);
        var (allow, deny) = Rules(file);

        Assert.Contains("read_url(docs.python.org)", allow);
        Assert.Contains("command(git push)", deny);
        Assert.Equal(["git status"], file.AllowedCommands);
    }

    [Fact]
    public void Duplicate_commands_are_written_once()
    {
        var file = AgySettings.Build(Worktree, new ExecutorPermissions { Commands = ["python -m unittest"], AllowGateCommands = true }, ["Python -m unittest"], Temp);
        Assert.Single(file.AllowedCommands);
    }
}

public class AgyExecutableDiscoveryTests
{
    [Fact]
    public void Agy_has_fallback_directories_for_the_installer_locations()
    {
        var dirs = ExecutableResolver.FallbackDirectories("agy", "/home/u").ToList();
        Assert.Contains(Path.Combine("/home/u", ".gemini", "bin"), dirs);
        Assert.Contains(Path.Combine("/home/u", ".local", "bin"), dirs);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("git")]
    [InlineData("python")]
    public void Other_commands_have_no_fallbacks(string command) =>
        Assert.Empty(ExecutableResolver.FallbackDirectories(command, "/home/u"));

    [Fact]
    public void Finds_agy_in_a_fallback_directory_when_it_is_not_on_PATH()
    {
        using var home = new TempDir();
        var bin = Path.Combine(home.Path, ".gemini", "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, OperatingSystem.IsWindows() ? "agy.exe" : "agy");
        File.WriteAllText(exe, "");

        var found = ExecutableResolver.FallbackDirectories("agy", home.Path)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "agy.exe" : "agy"))
            .FirstOrDefault(File.Exists);
        Assert.Equal(exe, found);
    }
}

public class ConfigExecutorAgyTests
{
    private sealed class ScriptedRunner(int exit, string stdout, string stderr = "") : IProcessRunner
    {
        public ProcessSpec? Spec { get; private set; }
        public string? SettingsDuring { get; private set; }
        public string? BriefDuring { get; private set; }

        public Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Spec = spec;
            var i = spec.Args.ToList().IndexOf("--settings");
            SettingsDuring = i >= 0 ? File.ReadAllText(spec.Args[i + 1]) : null;
            BriefDuring = File.ReadAllText(Path.Combine(spec.WorkingDirectory, "AGENTS.md"));
            if (spec.StdoutFile is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(spec.StdoutFile)!);
                File.WriteAllText(spec.StdoutFile, stdout);
            }

            return Task.FromResult(new ProcessOutcome { ExitCode = exit, TimedOut = false, Duration = TimeSpan.FromSeconds(1), Stdout = stdout, Stderr = stderr });
        }
    }

    private static ExecutorDefinition Definition() => new()
    {
        Cmd = ["agy", "-p", "{prompt}", "--output-format", "stream-json", "--settings", "{settings_file}", "--sandbox"],
        ModelArgs = ["--model", "{model}"],
        BriefFile = "AGENTS.md",
        UsageFormat = "agy-json",
        Capabilities = new ExecutorCapabilities { Headless = true, ModelOverride = true, ReportsTokens = true },
    };

    private static ExecutorSession Session(string logs) => new() { LogDirectory = logs, GateCommands = ["python -m unittest"] };

    private static Brief TheBrief() => new() { Prompt = "Read AGENTS.md", Body = "UNIT BRIEF", Label = "U1-a1" };

    [Fact]
    public async Task By_default_the_worker_gets_no_shell_commands_and_is_told_so()
    {
        using var tmp = new TempDir();
        var runner = new ScriptedRunner(0, AgyFixtures.CleanEdit(tmp.Path));

        await new ConfigExecutor("antigravity", Definition(), Session(Path.Combine(tmp.Path, "logs")), runner)
            .RunAsync(TheBrief(), tmp.Path, AgyFixtures.Model, TimeSpan.FromMinutes(1), default);

        Assert.DoesNotContain("command(", runner.SettingsDuring, StringComparison.Ordinal);
        Assert.Contains("NO shell commands", runner.BriefDuring, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_least_privilege_settings_outside_the_worktree_and_tells_the_worker_the_rules()
    {
        using var tmp = new TempDir();
        var work = Path.Combine(tmp.Path, "work");
        Directory.CreateDirectory(work);
        var logs = Path.Combine(tmp.Path, "logs");
        var runner = new ScriptedRunner(0, AgyFixtures.CleanEdit(work));
        var definition = Definition();
        definition.Permissions.AllowGateCommands = true;   // opt in, so the command rule and brief listing can be checked

        var result = await new ConfigExecutor("antigravity", definition, Session(logs), runner)
            .RunAsync(TheBrief(), work, AgyFixtures.Model, TimeSpan.FromMinutes(1), default);

        // The rules file is next to the logs, not in the worktree the worker can write.
        var settingsPath = runner.Spec!.Args[runner.Spec.Args.ToList().IndexOf("--settings") + 1];
        Assert.StartsWith(logs, settingsPath, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories), f => f.EndsWith(".agy-settings.json", StringComparison.Ordinal));
        Assert.Contains("command(python -m unittest)", runner.SettingsDuring, StringComparison.Ordinal);
        Assert.Contains("write_file(", runner.SettingsDuring, StringComparison.Ordinal);

        // No blanket trust anywhere on the command line.
        Assert.DoesNotContain("--dangerously-skip-permissions", runner.Spec.Args);
        Assert.DoesNotContain("--mode", runner.Spec.Args);
        Assert.Contains("--sandbox", runner.Spec.Args);
        Assert.Contains("--model", runner.Spec.Args);
        Assert.Equal(AgyFixtures.Model, runner.Spec.Args[runner.Spec.Args.ToList().IndexOf("--model") + 1]);

        // The brief carries the command policy; the brief file is removed afterwards.
        Assert.Contains("UNIT BRIEF", runner.BriefDuring, StringComparison.Ordinal);
        Assert.Contains("`python -m unittest`", runner.BriefDuring, StringComparison.Ordinal);
        Assert.Contains("FIRST tool call that is not allowed", runner.BriefDuring, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(work, "AGENTS.md")));

        Assert.Null(result.Fault);
        Assert.Equal(AgyFixtures.Model, result.ReportedModel);
        Assert.Equal("c1", result.Audit!.ConversationId);
        Assert.Equal(762, Assert.Single(result.Usage).OutputTokens);
    }

    [Fact]
    public async Task A_soft_denied_run_comes_back_faulted_with_the_audit_trail()
    {
        using var tmp = new TempDir();
        var runner = new ScriptedRunner(0, AgyFixtures.HeadlessDenied("python -c \"print(1)\""));

        var result = await new ConfigExecutor("antigravity", Definition(), Session(Path.Combine(tmp.Path, "logs")), runner)
            .RunAsync(TheBrief(), tmp.Path, AgyFixtures.Model, TimeSpan.FromMinutes(1), default);

        Assert.Equal(0, result.ExitCode);   // agy said exit 0 ...
        Assert.Equal(WorkerFaultKind.PermissionDenied, result.Fault?.Kind);   // ... but this is not a success
        Assert.Equal(["python -c \"print(1)\""], result.Audit!.Commands);
        Assert.Contains("command: RunCommand", result.Audit.Denied);
    }

    [Fact]
    public async Task A_model_mismatch_comes_back_as_a_fault()
    {
        using var tmp = new TempDir();
        var stdout = AgyFixtures.CleanEdit(tmp.Path).Replace(AgyFixtures.Model, "claude-sonnet-4-6", StringComparison.Ordinal);

        var result = await new ConfigExecutor("antigravity", Definition(), Session(Path.Combine(tmp.Path, "logs")), new ScriptedRunner(0, stdout))
            .RunAsync(TheBrief(), tmp.Path, AgyFixtures.Model, TimeSpan.FromMinutes(1), default);

        Assert.Equal(WorkerFaultKind.ModelMismatch, result.Fault?.Kind);
        Assert.Equal("claude-sonnet-4-6", result.ReportedModel);
    }

    [Fact]
    public async Task Verification_can_be_switched_off_explicitly()
    {
        using var tmp = new TempDir();
        var def = Definition();
        def.VerifyModel = false;
        var stdout = AgyFixtures.CleanEdit(tmp.Path).Replace(AgyFixtures.Model, "claude-sonnet-4-6", StringComparison.Ordinal);

        var result = await new ConfigExecutor("antigravity", def, Session(Path.Combine(tmp.Path, "logs")), new ScriptedRunner(0, stdout))
            .RunAsync(TheBrief(), tmp.Path, AgyFixtures.Model, TimeSpan.FromMinutes(1), default);

        Assert.Null(result.Fault);
    }

    [Fact]
    public async Task A_non_zero_exit_without_a_result_is_a_process_fault()
    {
        using var tmp = new TempDir();
        var result = await new ConfigExecutor("antigravity", Definition(), Session(Path.Combine(tmp.Path, "logs")), new ScriptedRunner(2, "", "agy: login required"))
            .RunAsync(TheBrief(), tmp.Path, AgyFixtures.Model, TimeSpan.FromMinutes(1), default);

        Assert.Equal(WorkerFaultKind.ProcessFailed, result.Fault?.Kind);
        Assert.Contains("login required", result.Fault!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Any_executor_with_a_non_zero_exit_is_faulted_not_just_agy()
    {
        using var tmp = new TempDir();
        tmp.File("AGENTS.md", "x");
        var def = new ExecutorDefinition { Cmd = ["agent", "{prompt}"], Capabilities = new ExecutorCapabilities { Headless = true } };

        var result = await new ConfigExecutor("agent", def, Session(Path.Combine(tmp.Path, "logs")), new ScriptedRunner(1, "", "crashed"))
            .RunAsync(TheBrief(), tmp.Path, null, TimeSpan.FromMinutes(1), default);

        Assert.Equal(WorkerFaultKind.ProcessFailed, result.Fault?.Kind);
    }

    [Fact]
    public async Task Inside_temp_the_run_carries_the_isolation_caveat()
    {
        using var tmp = new TempDir();   // lives under the system temp directory
        var result = await new ConfigExecutor("antigravity", Definition(), Session(Path.Combine(tmp.Path, "logs")), new ScriptedRunner(0, AgyFixtures.CleanEdit(tmp.Path)))
            .RunAsync(TheBrief(), tmp.Path, AgyFixtures.Model, TimeSpan.FromMinutes(1), default);

        Assert.Contains(result.Audit!.Notes, n => n.Contains("worktree_root", StringComparison.Ordinal));
    }
}
