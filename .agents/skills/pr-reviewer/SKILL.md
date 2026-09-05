---
name: pr-reviewer
description: 'Review pull requests created by the issue-fixer workflow. Use when: reviewing agent fixes, working through agent-needs-review issues, approving or requesting changes on automated PRs, preparing issues for merge.'
argument-hint: '[count|priority|issue#] — e.g. "3", "critical", "#82"'
---

# PR Reviewer

Reviews pull requests created by the issue-fixer workflow. Picks up open `agent-needs-review` labeled issues, reviews the linked PR, and either approves it (`agent-ready-for-merge`) or sends it back for changes (`agent-ready` with comments on the PR).

**Label lifecycle (review side):**
- `agent-needs-review` → fix is complete, PR is open, awaiting review
- `agent-in-progress-review` → review is actively happening (set on pickup, removed when done)
- `agent-ready-for-merge` → PR approved, ready for a human to merge
- `agent-changes-requested` → review requested changes; issue is back in the fixer queue for rework

**The reviewer never merges.** Merging is a human action — the merge closes the issue, which is the final state of the workflow.

## When to Use

- After an issue-fixer session has created PRs
- When the user says "review the agent's work", "check the PRs", or "work through the review queue"
- Periodic review sessions (e.g., after each fixer run)

## Procedure

### Step 1: Find Issues Awaiting Review

```bash
gh issue list --repo negativeeddy/arm-sharp \
  --label agent-needs-review --state open \
  --limit 200 --json number,title,labels
```

Sort by priority: critical → medium → low.

### Step 2: Pick Up the Issue

```bash
gh issue edit <number> --repo negativeeddy/arm-sharp --remove-label "agent-needs-review" --add-label "agent-in-progress-review"
```

### Step 3: Find the Linked PR

The fixer comments the PR URL on the issue. Read the issue thread, or look it up by branch:

```bash
gh issue view <number> --repo negativeeddy/arm-sharp --json comments --jq '.comments[].body'
# or, by branch name:
gh pr list --repo negativeeddy/arm-sharp --head fix/issue-#<number> --json number,url --jq '.[0]'
```

If the PR is already merged (a human merged it first), the issue should be closed — close it and move on:

```bash
gh issue close <number> --repo negativeeddy/arm-sharp --reason completed
```

### Step 4: Review the PR

- Check out the branch: `gh pr checkout <pr-number> --repo negativeeddy/arm-sharp`
- Read the diff: `gh pr diff <pr-number> --repo negativeeddy/arm-sharp`
- Verify the fix matches the issue's proposed fix
- Run the build: `dotnet build ArmRipper.slnx -c Debug`
- Run relevant tests: `dotnet test`
- Check for: regressions, style inconsistencies, missing tests, incomplete fixes

### Step 5: Finalize

**Approve** — the fix is correct and complete:

```bash
gh pr review <pr-number> --repo negativeeddy/arm-sharp --approve \
  --body "Reviewed and approved. Build and tests pass. Ready for merge."
gh issue edit <number> --repo negativeeddy/arm-sharp --remove-label "agent-in-progress-review" --add-label "agent-ready-for-merge"
```

**Request changes** — the fix needs work. Be specific about what must change; the fixer acts on these comments without further context:

```bash
gh pr review <pr-number> --repo negativeeddy/arm-sharp --request-changes \
  --body "<specific, actionable change requests>"
gh issue edit <number> --repo negativeeddy/arm-sharp --remove-label "agent-in-progress-review" --add-label "agent-changes-requested"
```

The `agent-changes-requested` label returns the issue to the fixer queue. On the next fixer run, the fixer reads the PR review comments, updates the existing branch/PR, and re-submits with `agent-needs-review`.

**Needs investigation** — the review reveals a deeper problem:

```bash
gh issue edit <number> --repo negativeeddy/arm-sharp --remove-label "agent-in-progress-review" --add-label "needs-investigation"
gh issue comment <number> --repo negativeeddy/arm-sharp --body "Review found this needs deeper analysis: <explanation>."
```

### Step 6: Report Summary

```
## PR Review Session Complete

**Approved (ready for merge):** N
**Changes requested (back to fixer):** N
**Needs investigation:** N

### Ready for Merge
| Issue | PR | Priority |
|-------|----|----------|
| #N | PR #NN | 🟡 Medium |

### Changes Requested
| Issue | PR | Summary of requested changes |
|-------|----|------------------------------|
| #M | PR #MM | <short summary> |
```

## Safety Rules

- **Never merge** — merging is a human action; the merge closes the issue
- **Verify before approving** — build and tests must pass
- **Be specific in change requests** — the fixer acts on the PR comments without further context
- **One issue at a time** — review each PR independently
- **Label lifecycle** — `agent-needs-review` → `agent-in-progress-review` (on pickup) → `agent-ready-for-merge` (approved) or `agent-changes-requested` (changes requested)

## Quick Reference Commands

```bash
# List issues awaiting review
gh issue list --repo negativeeddy/arm-sharp --label agent-needs-review --state open

# List issues currently being reviewed
gh issue list --repo negativeeddy/arm-sharp --label agent-in-progress-review --state open

# View a PR
gh pr view <pr-number> --repo negativeeddy/arm-sharp

# View a PR diff
gh pr diff <pr-number> --repo negativeeddy/arm-sharp

# Approve
gh pr review <pr-number> --repo negativeeddy/arm-sharp --approve --body "..."
gh issue edit <number> --repo negativeeddy/arm-sharp --remove-label "agent-in-progress-review" --add-label "agent-ready-for-merge"

# Request changes
gh pr review <pr-number> --repo negativeeddy/arm-sharp --request-changes --body "..."
gh issue edit <number> --repo negativeeddy/arm-sharp --remove-label "agent-in-progress-review" --add-label "agent-changes-requested"
```