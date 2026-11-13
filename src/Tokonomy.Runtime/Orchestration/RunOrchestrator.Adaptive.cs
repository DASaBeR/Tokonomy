using Tokonomy.Core.Abstractions;
using Tokonomy.Core.Model;
using Tokonomy.Core.Planning;
using Tokonomy.Core.Supervision;
using Tokonomy.Core.Takeover;
using Tokonomy.Llm.Architect;
using Tokonomy.Runtime.Executors;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Takeover;

namespace Tokonomy.Runtime.Orchestration;

/// <summary>
/// Adaptive supervision (strategy 5). The architect is called only on deterministic triggers; what it may do is
/// bounded by policy; every plan change goes through <see cref="PlanManager"/> validation; every edit it writes goes
/// through the guarded applier and the full gate suite; and every intervention is recorded from trigger to outcome.
/// </summary>
public sealed partial class RunOrchestrator
{
    private enum StepKind
    {
        /// <summary>Nothing changed; the deterministic decision stands (or, after a review, commit the unit).</summary>
        Proceed,

        /// <summary>The worker tries again with new feedback (corrected instructions, a partial correction, review findings).</summary>
        Resume,

        /// <summary>The unit was completed and committed by an architect edit.</summary>
        Accepted,

        /// <summary>The plan no longer contains the unit (it was split or replaced); the worktree is back at the unit start.</summary>
        Replaced,

        /// <summary>Only a human can decide; the unit stops.</summary>
        Human,

        Failed,
    }

    private sealed record Step(StepKind Kind, string Note = "", string? Feedback = null);

    /// <summary>What the orchestrator tracks about one unit across attempts and interventions.</summary>
    private sealed class UnitTrack
    {
        public int Interventions { get; set; }
        public bool InstructionCorrectionUsed { get; set; }
        public string? LastFailure { get; set; }
        public int ReviewRequests { get; set; }
        public UnitReviewVerdict? Review { get; set; }
        public List<string> Notes { get; } = [];
    }

    private sealed class RunContext
    {
        public required RunRequest Request { get; init; }
        public required RunWorkspace Workspace { get; init; }
        public required WorkspaceWatch Watch { get; init; }
        public required IExecutor Executor { get; init; }
        public required GateSession Gates { get; init; }
        public required Dictionary<string, Tokonomy.Runtime.Gates.GateRun> Baseline { get; init; }
        public required PlanManager Plan { get; init; }
        public required BudgetGuard Budget { get; init; }
        public required IRunObserver Observer { get; init; }
        public string? AcceptanceDir { get; init; }
        public List<WorkUnit> Completed { get; } = [];
        public List<LockedRegion> Locks { get; } = [];
        public List<Handoff> Handoffs { get; } = [];
        public int RunInterventions { get; set; }
        public int Splits { get; set; }

        /// <summary>Per-unit history restored by <c>run --resume</c> (empty for a fresh run).</summary>
        public IReadOnlyDictionary<string, UnitHistory> History { get; set; } = new Dictionary<string, UnitHistory>();

        /// <summary>Units whose acceptance tests are already committed on the branch.</summary>
        public IReadOnlySet<string> TestsCommitted { get; set; } = new HashSet<string>();

        public int Resumes { get; set; }
        public string? RecoveryError { get; set; }

        /// <summary>Adaptive supervision needs escalation and gates: without gates nothing verifies an architect edit.</summary>
        public bool Adaptive => Request.AdaptiveSupervision && Request.AllowEscalation && Request.UseGates;
    }

    private static string ArchitectModel(RunRequest request) => request.ArchitectModel ?? request.Policy.Models.Architect;

    /// <summary>
    /// Calls the architect on a deterministic trigger, validates its decision against the allowed levels, applies its
    /// plan updates and lessons, and carries out the chosen level.
    /// </summary>
    private async Task<Step> SuperviseAsync(
        RunContext ctx, string unitId, UnitState state, UnitTrack track, SupervisionRequest call, Decision decision, WorkerReport? report,
        UnitDiff diff, List<GateResult> gateResults, PlanDeviation deviation, string unitStart, IReadOnlyList<ReviewFinding> reviewFindings, CancellationToken ct)
    {
        var request = ctx.Request;
        var policy = request.Policy;
        var model = ArchitectModel(request);
        var unit = ctx.Plan.Unit(unitId);
        track.Interventions++;
        ctx.RunInterventions++;
        SaveRunState(ctx, RunPhases.Supervision, unitId, unitStart);
        ctx.Observer.SupervisionStarted(unit, call);
        ledger.UpsertUnit(request.RunId, unit.Id, unit.Title, "supervision", state.Attempts, state.Takeovers);
        decimal cost = 0;
        int? revision = null;
        var evidence = (report?.Evidence ?? []).Concat(diff.ChangedPaths).Distinct(StringComparer.Ordinal).Take(20).ToList();

        Step Finish(int level, string outcome, string diagnosis, string action, string rootCause, Step step)
        {
            var record = new InterventionRecord
            {
                RunId = request.RunId,
                UnitId = unitId,
                At = _clock.UtcNow,
                Trigger = TriggerName(call.Trigger),
                Level = level,
                RootCause = rootCause,
                Diagnosis = diagnosis,
                Action = action,
                Outcome = outcome,
                PlanRevision = revision,
                Evidence = evidence,
                Usd = cost,
            };
            ledger.RecordIntervention(record);
            request.Artifacts.Append("intervention", record);
            ctx.Observer.InterventionFinished(unit, record);
            track.Notes.Add($"L{level} after {TriggerName(call.Trigger)} -> {outcome}: {Shorten(diagnosis, 300)}");
            return step;
        }

        var input = new SupervisionInput
        {
            Plan = ctx.Plan.Current,
            Revision = ctx.Plan.Revision,
            Unit = unit,
            Request = call,
            Attempts = state.Attempts,
            Report = report,
            Diff = diff.Patch,
            FailureOutput = gateResults.Any(g => g.Blocking) ? BriefWriter.Feedback(decision.Reason, gateResults) : "",
            Files = ReadRelevantFiles(ctx.Workspace.Path, diff.ChangedPaths.Concat(unit.ExpectedFiles).Concat(report?.Evidence ?? [])),
            RepoFiles = await ListRepoFilesAsync(ctx.Workspace.Path, ct),
            Locked = ctx.Locks,
            Lessons = ctx.Plan.Lessons,
            Deviations = deviation.Describe().ToList(),
            PreviousInterventions = track.Notes,
            ReviewFindings = reviewFindings,
            Remaining = ctx.Plan.Remaining().Where(u => u.Id != unitId).ToList(),
            Completed = ctx.Completed,
        };

        string text;
        try
        {
            var response = await architect!.CompleteAsync(new ProviderRequest(model, SupervisorPrompt.System, SupervisorPrompt.Build(input)), ct);
            RecordArchitectCall(request.RunId, unitId, response, ctx.Budget, CallPurposes.Supervision);
            cost = response.Usd ?? 0;
            text = response.Text;
            request.Artifacts.WriteLog($"{unitId}-supervision{track.Interventions}.reply.md", text);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.ClientModel.ClientResultException)
        {
            ctx.Observer.Warning($"The architect could not be reached for supervision: {ex.Message}");
            return Finish(-1, "unavailable", $"the architect call failed: {ex.Message}", "", "unclear", new Step(StepKind.Proceed));
        }

        var reply = SupervisorPrompt.Parse(text, call.AllowedLevels, unitId);
        if (reply.HumanDecision is { } human)
        {
            return Finish((int)reply.Level, "human", reply.Diagnosis.Length > 0 ? reply.Diagnosis : human, human, reply.RootCause,
                new Step(StepKind.Human, $"needs a human decision: {human}"));
        }

        if (!reply.Usable)
        {
            ctx.Observer.Warning($"The architect's supervision reply was not usable: {string.Join("; ", reply.Problems)}");
            return Finish(-1, "rejected", reply.Diagnosis, "reply rejected: " + string.Join("; ", reply.Problems), reply.RootCause, new Step(StepKind.Proceed));
        }

        // Plan updates and lessons go in as two atomic revisions, so a rejected plan change never loses the lessons.
        var trigger = $"{unitId}:{TriggerName(call.Trigger)}";
        var reason = reply.Diagnosis.Length > 0 ? reply.Diagnosis : call.Reason;
        if (reply.PlanUpdates.Count > 0 && ApplyPlan(ctx, "supervision", reason, trigger, reply.RootCause, evidence, reply.PlanUpdates) is { Applied: true } updated)
        {
            revision = updated.Revision;
        }

        var notes = reply.Lessons.ToList();
        if (reply.Instructions is { } instructions && reply.Level is InterventionLevel.InstructionCorrection or InterventionLevel.GuidedCorrection &&
            ctx.Plan.Contains(unitId))
        {
            notes.Add(new PlanOperation { Op = PlanOps.UpdateUnit, Unit = unitId, Corrections = [Shorten(instructions, PlanManager.MaxTextLength)] });
        }

        if (notes.Count > 0 && ApplyPlan(ctx, "supervision", $"lessons and corrections after {TriggerName(call.Trigger)} on {unitId}", trigger, reply.RootCause, evidence, notes) is { Applied: true } learned)
        {
            revision = learned.Revision;
        }

        if (!ctx.Plan.Contains(unitId))
        {
            await ctx.Workspace.ResetAsync(unitStart, CancellationToken.None, ctx.Watch.KnownLinks);
            return Finish((int)reply.Level, "replaced", reply.Diagnosis, "the unit was replaced in the plan", reply.RootCause,
                new Step(StepKind.Replaced, $"replaced in plan revision {ctx.Plan.Revision}: {Shorten(reason, 200)}"));
        }

        switch (reply.Level)
        {
            case InterventionLevel.None:
                return Finish(0, "no_action", reply.Diagnosis, "no change", reply.RootCause, new Step(StepKind.Proceed));

            case InterventionLevel.InstructionCorrection:
                track.InstructionCorrectionUsed = true;
                if (reply.DiscardWorkerChanges)
                {
                    await ctx.Workspace.ResetAsync(unitStart, CancellationToken.None, ctx.Watch.KnownLinks);
                }

                var feedback = Instruct(reply, decision, gateResults, reply.DiscardWorkerChanges);
                return Finish(1, "resumed", reply.Diagnosis, (reply.DiscardWorkerChanges ? "worker attempt discarded; " : "") + "corrected instructions given",
                    reply.RootCause, new Step(StepKind.Resume, Feedback: feedback));
        }

        // L2 / L3: the architect's own edits, through the same guards as a takeover.
        var level = (int)reply.Level;
        var kind = reply.Level == InterventionLevel.Takeover ? "takeover" : "correction";
        var protectedHits = reply.Edits.Select(e => PlanPaths.Normalize(e.Path) ?? e.Path)
            .Where(p => Glob.AnyMatch(policy.Escalation.ProtectedPaths, p) || (ctx.AcceptanceDir is { } dir && p.StartsWith(dir + "/", StringComparison.Ordinal)))
            .ToList();
        if (protectedHits.Count > 0)
        {
            return Finish(level, "rejected", reply.Diagnosis, $"edits target protected paths: {string.Join(", ", protectedHits)}", reply.RootCause,
                reply.Instructions is null ? new Step(StepKind.Proceed) : new Step(StepKind.Resume, Feedback: Instruct(reply, decision, gateResults, false)));
        }

        var applied = EditApplier.Apply(ctx.Workspace.Path, reply.Edits);
        if (!applied.Ok)
        {
            // All-or-nothing: nothing was written, so the worker can safely continue from where it was.
            return Finish(level, "rejected", reply.Diagnosis, "edits could not be applied: " + string.Join("; ", applied.Errors), reply.RootCause,
                reply.Instructions is null ? new Step(StepKind.Proceed) : new Step(StepKind.Resume, Feedback: Instruct(reply, decision, gateResults, false)));
        }

        if (reply.Level == InterventionLevel.Takeover)
        {
            state.Takeovers++;
        }

        // A dependency the user did not approve is a human decision, whoever adds it.
        var check = await ctx.Workspace.DiffAsync(unitStart, ct);
        if (check.Dependencies.Unapproved(policy.Escalation.AllowedDependencies) is { Any: true } deps)
        {
            await ctx.Workspace.ResetAsync(unitStart, CancellationToken.None, ctx.Watch.KnownLinks);
            var names = deps.Names.Count > 0 ? string.Join(", ", deps.Names) : "a manifest or lockfile change";
            return Finish(level, "human", reply.Diagnosis, $"the architect's edit adds dependencies: {names}", reply.RootCause,
                new Step(StepKind.Human, $"the architect's correction adds dependencies ({names}); approve them with escalation.allowed_dependencies"));
        }

        var verified = await VerifyArchitectEditAsync(ctx, unit, unitStart, state, model, reply.Edits, applied, kind, TakeoverTrigger.None, ct);
        var summary = reply.Summary ?? (reply.Diagnosis.Length > 0 ? Shorten(reply.Diagnosis, 200) : $"the architect {kind}");
        if (verified.Ok)
        {
            // Architect-written changes on risky units get an independent review before they are committed.
            var after = await ctx.Workspace.DiffAsync(unitStart, ct);
            var reviewed = await ReviewAsync(ctx, unit, state, track, report, after, verified.Gates,
                PlanDeviation.Detect(unit, after.ChangedPaths, ctx.AcceptanceDir), unitStart, ct, architectChange: true);
            switch (reviewed.Kind)
            {
                case StepKind.Resume:
                    // The fix passed the gates but not the review: the worker continues from it with the findings.
                    return Finish(level, "resumed", reply.Diagnosis, $"{kind} passed the gates; the independent review asked for changes", reply.RootCause, reviewed);
                case StepKind.Failed or StepKind.Human or StepKind.Replaced:
                    await ctx.Workspace.ResetAsync(unitStart, CancellationToken.None, ctx.Watch.KnownLinks);
                    return Finish(level, reviewed.Kind == StepKind.Human ? "human" : "failed", reply.Diagnosis,
                        $"{kind} passed the gates but the independent review stopped it: {reviewed.Note}", reply.RootCause, reviewed with { Kind = reviewed.Kind == StepKind.Replaced ? StepKind.Failed : reviewed.Kind });
            }

            var locks = reply.Level == InterventionLevel.Takeover ? LocksOf(reply.Edits) : [];
            await CommitArchitectEditAsync(ctx, unit, applied.Files, summary, reply.Why ?? reply.Diagnosis, locks,
                reply.Level == InterventionLevel.Takeover ? "[architect takeover]" : "[architect correction]", ct);
            if (reply.Level == InterventionLevel.Takeover)
            {
                ctx.Observer.TakeoverFinished(unit, true, summary);
            }

            return Finish(level, "accepted", reply.Diagnosis, $"{kind} applied to {string.Join(", ", applied.Files)}; gates pass", reply.RootCause,
                new Step(StepKind.Accepted, $"architect {kind}: {summary}"));
        }

        if (reply.Level == InterventionLevel.GuidedCorrection)
        {
            // The correction is fully applied (never a partial patch); the worker continues from the corrected state.
            var resume = $"The architect applied a correction: {summary}\nContinue from the current state of the files.\n" +
                         (reply.Instructions is null ? "" : $"Instructions: {reply.Instructions}\n") +
                         BriefWriter.Feedback(verified.Note, verified.Gates);
            return Finish(2, "resumed", reply.Diagnosis, $"correction applied to {string.Join(", ", applied.Files)}; {verified.Note}", reply.RootCause,
                new Step(StepKind.Resume, Feedback: resume));
        }

        // A takeover that did not pass: back to the verified unit start, then let the worker resume if policy allows.
        await ctx.Workspace.ResetAsync(unitStart, CancellationToken.None, ctx.Watch.KnownLinks);
        ctx.Observer.TakeoverFinished(unit, false, verified.Note);
        if (policy.Supervision.ResumeAfterFailedTakeover && track.Interventions < policy.Supervision.MaxInterventionsPerUnit &&
            ctx.RunInterventions < policy.Supervision.MaxInterventionsPerRun)
        {
            var resume = $"The architect's own fix did not pass ({verified.Note}); the unit was reset to its start.\n" +
                         $"Diagnosis: {reply.Diagnosis}\n" + (reply.Instructions is null ? "" : $"Instructions: {reply.Instructions}\n");
            return Finish(3, "resumed", reply.Diagnosis, $"takeover failed ({verified.Note}); unit reset, worker resumes", reply.RootCause,
                new Step(StepKind.Resume, Feedback: resume));
        }

        return Finish(3, "failed", reply.Diagnosis, $"takeover failed: {verified.Note}", reply.RootCause,
            new Step(StepKind.Failed, $"{decision.Reason}; architect takeover failed: {verified.Note}"));
    }

    /// <summary>
    /// The risk-based architectural review of a unit whose gates all passed. It can approve, send the unit back to the
    /// worker (bounded), escalate to supervision, or stop it for a human; it can never accept a unit the gates rejected.
    /// </summary>
    private async Task<Step> ReviewAsync(
        RunContext ctx, WorkUnit unit, UnitState state, UnitTrack track, WorkerReport? report, UnitDiff diff, List<GateResult> gateResults,
        PlanDeviation deviation, string unitStart, CancellationToken ct, bool architectChange = false)
    {
        var request = ctx.Request;
        var policy = request.Policy;
        var signals = new ReviewSignals
        {
            Risk = unit.Risk,
            DiffLines = diff.TotalLines,
            ChangedFiles = diff.ChangedPaths.Count,
            Attempts = state.Attempts,
            Intervened = track.Interventions > 0,
            ReportedDiscovery = report?.HasDiscovery == true,
            DependencyChanged = diff.Dependencies.Any,
            Deviation = deviation,
        };
        var (should, why) = architectChange ? ReviewPolicy.ShouldReviewArchitectChange(policy.Review, signals) : ReviewPolicy.ShouldReview(policy.Review, signals);
        if (!should)
        {
            request.Artifacts.Append("review_skipped", new { unit = unit.Id, why });
            return new Step(StepKind.Proceed);
        }

        if (ctx.Budget.Exhausted)
        {
            return Unavailable("budget exhausted");
        }

        var model = architectChange && !string.IsNullOrWhiteSpace(policy.Review.ArchitectReviewModel) ? policy.Review.ArchitectReviewModel : ArchitectModel(request);
        if (architectChange)
        {
            why += ". You did not write this change: it was written by the supervising architect, not by the worker; review it as an outsider";
        }

        SaveRunState(ctx, RunPhases.Review, unit.Id, unitStart);
        ctx.Observer.Stage($"Review: the architect ({model}) checks {unit.Id} ({why})");
        UnitReviewReply reply;
        try
        {
            var prompt = UnitReviewPrompt.Build(new UnitReviewInput
            {
                Plan = ctx.Plan.Current,
                Revision = ctx.Plan.Revision,
                Unit = unit,
                Diff = diff.Patch,
                Why = why,
                Deviations = deviation.Describe().ToList(),
                Gates = gateResults.Select(g => $"{g.Gate}: {g.Status}").ToList(),
                Report = report,
                Lessons = ctx.Plan.LessonsFor(unit.Id),
            });
            var response = await architect!.CompleteAsync(new ProviderRequest(model, UnitReviewPrompt.System, prompt, JsonOutput: true), ct);
            RecordArchitectCall(request.RunId, unit.Id, response, ctx.Budget, CallPurposes.Review);
            request.Artifacts.WriteLog($"{unit.Id}-review{track.ReviewRequests + 1}.reply.md", response.Text);
            reply = UnitReviewPrompt.Parse(response.Text, response.Model, unit.Id);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.ClientModel.ClientResultException)
        {
            return Unavailable($"the architect call failed: {ex.Message}");
        }

        var review = reply.Review;
        Record(review);
        var evidence = review.Findings.Select(f => f.File).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (reply.PlanUpdates.Count > 0)
        {
            ApplyPlan(ctx, "review", $"review of {unit.Id}: {review.Summary}", $"{unit.Id}:review", review.Verdict.ToString(), evidence, reply.PlanUpdates);
        }

        if (reply.Lessons.Count > 0)
        {
            ApplyPlan(ctx, "review", $"lessons from the review of {unit.Id}", $"{unit.Id}:review", review.Verdict.ToString(), evidence, reply.Lessons);
        }

        if (!ctx.Plan.Contains(unit.Id))
        {
            await ctx.Workspace.ResetAsync(unitStart, CancellationToken.None, ctx.Watch.KnownLinks);
            return new Step(StepKind.Replaced, $"replaced by the review in plan revision {ctx.Plan.Revision}");
        }

        switch (review.Verdict)
        {
            case UnitReviewVerdict.Approve or UnitReviewVerdict.ApproveWithWarnings:
                return new Step(StepKind.Proceed);
            case UnitReviewVerdict.Unavailable:
                return Unavailable(review.Summary);
            case UnitReviewVerdict.Blocked:
                return new Step(StepKind.Failed, $"the architectural review blocked the unit: {review.Summary} {Findings(review)}".Trim());
            case UnitReviewVerdict.HumanDecisionRequired:
                return new Step(StepKind.Human, $"the architectural review needs a human decision: {review.Summary} {Findings(review)}".Trim());
        }

        // Request changes: first back to the worker with the findings, then to the supervisor.
        if (track.ReviewRequests < policy.Review.MaxChangeRequestsPerUnit)
        {
            track.ReviewRequests++;
            return new Step(StepKind.Resume, Feedback:
                $"Every automated check passed, but the architect's review requests changes before the unit is accepted: {review.Summary}\n" +
                string.Join("\n", review.Findings.Select(f => "- " + f)) + "\nKeep what passes the checks; fix these points.");
        }

        if (architectChange)
        {
            // No escalation back to the architect whose change is under review.
            return new Step(StepKind.Failed, $"the independent review still requests changes to the architect's own fix: {review.Summary}");
        }

        var call = InterventionPolicy.Evaluate(new SupervisionContext
        {
            Decision = new Decision(DecisionKind.Accept, "all gates passed"),
            ReviewRejected = true,
            UnitInterventions = track.Interventions,
            RunInterventions = ctx.RunInterventions,
            Takeovers = state.Takeovers,
            InstructionCorrectionUsed = track.InstructionCorrectionUsed,
        }, policy.Supervision, policy.Escalation);
        if (call is null)
        {
            return new Step(StepKind.Failed, $"the architectural review still requests changes after {track.ReviewRequests} round(s): {review.Summary}");
        }

        var step = await SuperviseAsync(ctx, unit.Id, state, track, call, new Decision(DecisionKind.Accept, "all gates passed"), report, diff,
            gateResults, deviation, unitStart, review.Findings, ct);
        return step.Kind == StepKind.Proceed ? new Step(StepKind.Proceed) : step;

        Step Unavailable(string detail)
        {
            var unavailable = new UnitReview(UnitReviewVerdict.Unavailable, detail, [], ArchitectModel(request));
            Record(unavailable);
            if (policy.Review.OnUnavailable == Tokonomy.Core.Config.ReviewConfig.OnUnavailableFail)
            {
                return new Step(StepKind.Failed, $"the required architectural review is unavailable: {detail}");
            }

            ctx.Observer.Warning($"Review of {unit.Id} unavailable ({detail}); accepted on the gates.");
            return new Step(StepKind.Proceed);
        }

        void Record(UnitReview r)
        {
            track.Review = r.Verdict;
            ledger.RecordUnitReview(new UnitReviewRecord(request.RunId, unit.Id, _clock.UtcNow, r.Verdict, r.Summary, why, r.Findings));
            request.Artifacts.Append("unit_review", new { unit = unit.Id, verdict = r.Verdict.ToString(), r.Summary, why, findings = r.Findings });
            ctx.Observer.ReviewFinished(unit, r, why);
        }
    }

    private bool CanDecompose(RunContext ctx, WorkUnit unit) =>
        ctx.Adaptive && architect is not null && unit.SplitFrom is null && ctx.Splits < ctx.Request.Policy.Supervision.MaxSplitsPerRun && !ctx.Budget.Exhausted;

    /// <summary>
    /// Splits an oversized unit through a validated plan revision and resets the worktree to the unit start. Returns a
    /// note on success, null when the split could not be made (the unit then fails as before).
    /// </summary>
    private async Task<string?> DecomposeAsync(
        RunContext ctx, WorkUnit unit, Decision decision, UnitDiff diff, List<GateResult> gateResults, string unitStart, CancellationToken ct)
    {
        var request = ctx.Request;
        var model = ArchitectModel(request);
        SaveRunState(ctx, RunPhases.Decomposition, unit.Id, unitStart);
        ctx.Observer.Stage($"Split: the architect ({model}) breaks {unit.Id} into smaller units ({decision.Reason})");
        string text;
        decimal cost;
        try
        {
            var prompt = DecomposePrompt.Build(ctx.Plan.Current, unit, decision.Reason, diff.ChangedPaths, diff.TotalLines, BriefWriter.Feedback(decision.Reason, gateResults));
            var response = await architect!.CompleteAsync(new ProviderRequest(model, DecomposePrompt.System, prompt, JsonOutput: true), ct);
            RecordArchitectCall(request.RunId, unit.Id, response, ctx.Budget, CallPurposes.Decomposition);
            request.Artifacts.WriteLog($"{unit.Id}-split.reply.md", response.Text);
            text = response.Text;
            cost = response.Usd ?? 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.ClientModel.ClientResultException)
        {
            ctx.Observer.Warning($"The architect could not split {unit.Id}: {ex.Message}");
            return null;
        }

        var (parts, reason) = DecomposePrompt.Parse(text);
        if (parts.Count == 0)
        {
            ctx.Observer.Warning($"The architect's split of {unit.Id} could not be read.");
            return null;
        }

        var result = ApplyPlan(ctx, "decomposition", reason.Length > 0 ? reason : decision.Reason, $"{unit.Id}:oversized", reason,
            diff.ChangedPaths.Take(20).ToList(), [new PlanOperation { Op = PlanOps.SplitUnit, Unit = unit.Id, Parts = parts }]);
        if (!result.Applied)
        {
            return null;
        }

        ctx.Splits++;
        await ctx.Workspace.ResetAsync(unitStart, CancellationToken.None, ctx.Watch.KnownLinks);
        var ids = string.Join(", ", parts.Select(p => p.Id));
        var record = new InterventionRecord
        {
            RunId = request.RunId,
            UnitId = unit.Id,
            At = _clock.UtcNow,
            Trigger = "oversized",
            Level = 0,
            RootCause = "plan_error",
            Diagnosis = reason,
            Action = $"split into {ids}",
            Outcome = "split",
            PlanRevision = result.Revision,
            Evidence = diff.ChangedPaths.Take(20).ToList(),
            Usd = cost,
        };
        ledger.RecordIntervention(record);
        request.Artifacts.Append("intervention", record);
        ctx.Observer.InterventionFinished(unit, record);
        return $"split into {ids} (plan revision {result.Revision})";
    }

    /// <summary>Validates and applies a plan update atomically, then records, persists and announces the new revision.</summary>
    private PlanUpdateResult ApplyPlan(
        RunContext ctx, string source, string reason, string? trigger, string? decision, IReadOnlyList<string> evidence, IReadOnlyList<PlanOperation> operations)
    {
        var request = ctx.Request;
        var result = ctx.Plan.Apply(new PlanUpdate
        {
            BaseRevision = ctx.Plan.Revision,
            Source = source,
            Reason = Shorten(reason, 2000),
            Trigger = trigger,
            Decision = decision,
            Evidence = evidence,
            Operations = operations,
        });
        if (result.Applied && result.Record is { } record)
        {
            // The plan state is saved first: after a crash the ledger can only lag behind it, and resume re-records the gap.
            SavePlanState(ctx);
            ledger.RecordPlanRevision(request.RunId, record);
            request.Artifacts.Append("plan_revision", new { record.Number, record.Source, record.Reason, record.Trigger, changes = record.Changes.Count, lessons = result.NewLessons?.Select(l => l.Id) });
            ctx.Observer.PlanUpdated(record);
        }
        else
        {
            request.Artifacts.Append("plan_update_rejected", new { source, trigger, errors = result.Errors });
            ctx.Observer.Warning($"A plan update from the {source} was rejected: {string.Join("; ", result.Errors.Take(3))}");
        }

        return result;
    }

    private void RecordRecovery(RunContext ctx, WorkUnit unit, Decision decision, string note)
    {
        var record = new InterventionRecord
        {
            RunId = ctx.Request.RunId,
            UnitId = unit.Id,
            At = _clock.UtcNow,
            Trigger = "takeover_failed",
            Level = 3,
            RootCause = "unclear",
            Diagnosis = $"{decision.Reason}; takeover failed: {note}",
            Action = "unit reset to its start; the worker resumes with the findings",
            Outcome = "resumed",
        };
        ledger.RecordIntervention(record);
        ctx.Request.Artifacts.Append("intervention", record);
        ctx.Observer.InterventionFinished(unit, record);
    }

    private static void SavePlanState(RunContext ctx) => ctx.Request.Artifacts.SavePlanState(ctx.Plan.Snapshot());

    /// <summary>Saves the orchestrator's checkpoint (see <see cref="RunState"/>).</summary>
    private void SaveRunState(RunContext ctx, string phase, string? unitId = null, string? unitStart = null) =>
        ctx.Request.Artifacts.SaveRunState(new RunState
        {
            RunId = ctx.Request.RunId,
            Phase = phase,
            CurrentUnit = unitId,
            UnitStart = unitStart,
            PlanRevision = ctx.Plan.Revision,
            Locks = ctx.Locks.ToList(),
            Handoffs = ctx.Handoffs.ToList(),
            RunInterventions = ctx.RunInterventions,
            Splits = ctx.Splits,
            Updated = _clock.UtcNow,
            Resumes = ctx.Resumes,
        });

    /// <summary>
    /// Restores a resumed run's context from the reconciled state, then proves the accepted units still pass: every gate
    /// (plus the locked-region check) runs on the branch tip against the base-commit baseline. A tip that fails is not
    /// built on; the run stops and says why.
    /// </summary>
    private async Task<bool> RestoreAsync(RunContext ctx, RecoveryDecision recovery, CancellationToken ct)
    {
        var request = ctx.Request;
        var run = recovery.Run!;
        ctx.Locks.AddRange(run.Locks);
        ctx.Handoffs.AddRange(run.Handoffs);
        ctx.RunInterventions = run.RunInterventions;
        ctx.Splits = run.Splits;
        ctx.Resumes = run.Resumes + 1;
        ctx.History = recovery.History;
        ctx.TestsCommitted = recovery.TestsCommitted;
        var accepted = ContractValidator.Order(ctx.Plan.Current).Where(u => ctx.Plan.ProgressOf(u.Id) == UnitProgress.Accepted).ToList();
        ctx.Completed.AddRange(accepted);
        if (ctx.Gates.Acceptance is { } acceptanceFiles)
        {
            foreach (var unit in accepted)
            {
                acceptanceFiles.Add(unit);
            }
        }

        ledger.RecordEvent(request.RunId, _clock.UtcNow, "resumed", System.Text.Json.JsonSerializer.Serialize(new
        {
            resume = ctx.Resumes,
            accepted = accepted.Select(u => u.Id),
            revision = ctx.Plan.Revision,
            notes = recovery.Notes,
        }));

        if (accepted.Count == 0 || !request.UseGates)
        {
            return true;
        }

        ctx.Observer.Stage($"Resuming: re-checking the {accepted.Count} accepted unit(s) on the branch tip");
        var tip = await ctx.Workspace.DiffAsync(request.BaseSha, ct);
        var (results, _) = await RunGatesAsync(request, "resume-check", ctx.Workspace.Path, tip, ctx.Gates, ctx.Baseline, ctx.Locks, ct);
        request.Artifacts.Append("resume_check", new { gates = results.Select(g => new { g.Gate, g.Status }) });
        var failing = results.Where(g => g.Blocking).Select(g => g.Gate).ToList();
        if (failing.Count == 0)
        {
            return true;
        }

        ctx.RecoveryError = $"the accepted units no longer pass their gates on the branch tip ({string.Join(", ", failing)}); " +
                            "the branch or the environment changed since the interruption. Inspect the branch before continuing by hand.";
        return false;
    }

    private static string Instruct(SupervisorReply reply, Decision decision, List<GateResult> gateResults, bool discarded)
    {
        var text = $"The architect reviewed your attempt.\nDiagnosis: {reply.Diagnosis}\nWhat to do now: {reply.Instructions}\n" +
                   (discarded ? "Your previous changes were discarded; start from the unit start.\n" : "Your changes are still in the working tree.\n");
        return gateResults.Any(g => g.Blocking) && !discarded ? text + "\n" + BriefWriter.Feedback(decision.Reason, gateResults) : text;
    }

    private static string Findings(UnitReview review) =>
        review.Findings.Count == 0 ? "" : "(" + string.Join("; ", review.Findings.Take(3).Select(f => f.ToString())) + ")";

    public static string TriggerName(SupervisionTrigger trigger) => trigger switch
    {
        SupervisionTrigger.WorkerBlocked => "worker_blocked",
        SupervisionTrigger.ClarificationRequired => "clarification_required",
        SupervisionTrigger.ScopeDeviation => "scope_deviation",
        SupervisionTrigger.RepeatedFailure => "repeated_failure",
        SupervisionTrigger.RetryLimit => "retry_limit",
        SupervisionTrigger.NoChanges => "no_changes",
        SupervisionTrigger.Timeout => "timeout",
        SupervisionTrigger.WorkerFault => "worker_fault",
        _ => "review_rejected",
    };

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";
}
