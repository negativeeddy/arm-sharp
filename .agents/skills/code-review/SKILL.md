---
name: code-review
description: 'Perform a code review of the ARM-Sharp codebase and create GitHub issues for each finding. Use when: running a code review, generating cleanup tasks, auditing code quality, creating issues from code analysis, periodic review sweeps.'
argument-hint: '[scope] — e.g. "full codebase", "ArmRipper.Core", "recent changes"'
---

# Code Review → GitHub Issues

Performs a structured code review of the ARM-Sharp codebase and creates labeled GitHub issues for each finding. Issues are tagged with `code-review`, `agent-ready`, a priority label, and optionally `needs-investigation`.

## When to Use

- Periodic code quality sweeps (weekly/monthly)
- After major feature merges
- Before release milestones
- When the user asks to "run a code review" or "generate cleanup tasks"

## Procedure

### Step 1: Determine Scope

Ask the user what to review (or infer from the argument hint):

| Scope | Description |
|-------|-------------|
| `full` | Entire codebase — all projects under `src/` and `tests/` |
| `project` | Single project (e.g., `ArmRipper.Core`) |
| `recent` | Changes since last review (check `git log` and existing issues) |
| `file` | A specific file or set of files |

If no scope specified, default to `recent` (changes since the last `code-review` labeled issue).

### Step 2: Perform the Review

Use a read-only subagent (Explore) to examine the target code. Look for these categories:

**Critical (must-fix — correctness / data-loss risk):**
- Sync-over-async patterns (`.GetAwaiter().GetResult()`)
- Thread-safety issues (unsynchronized shared state)
- Missing error handling or swallowed exceptions
- Data loss or corruption risks

**Medium (correctness / maintainability):**
- Magic strings instead of enums
- Brittle or duplicated code
- Copy-paste errors
- Hardcoded values that should be configurable

**Low (polish / consistency / minor perf):**
- Inconsistent patterns
- Missing pre-sizing or minor perf wins
- Misleading comments
- Dead code

**Deep Investigation (needs deeper review):**
- Complex methods not yet fully reviewed
- External integration edge cases
- Parser fragility
- Concurrency patterns needing stress testing

For each finding, collect:
1. **Title** — concise, action-oriented
2. **Priority** — critical / medium / low
3. **Category** — one of the categories above
4. **File(s)** — affected file paths
5. **Problem** — what's wrong
6. **Proposed Fix** — how to fix it (if prescribable)
7. **Needs Investigation** — true if deeper review is required before a fix

### Step 3: Check for Duplicates

Before creating issues, check existing issues (both open and in-progress):

```bash
gh issue list --repo negativeeddy/arm-sharp --label code-review --state open --limit 200 --json number,title
```

Skip any finding that matches an existing open issue's title or description. The `code-review` label tracks all findings; `agent-ready` indicates the issue is available for automated fixing.

### Step 4: Create GitHub Issues

For each new finding, create a GitHub issue:

```bash
gh issue create --repo negativeeddy/arm-sharp \
  --title "<title>" \
  --body-file <temp-file> \
  --label "code-review" \
  --label "agent-ready" \
  --label "priority: <critical|medium|low>" \
  [--label "needs-investigation"]  # if deeper review needed
```

#### Issue Body Template

```markdown
## <Title>

**Source:** Code review (automated)
**Date:** <today>
**Scope:** <what was reviewed>
**File(s):** `<affected files>`

### Problem

<description of the issue>

### Proposed Fix

<fix description or code sample>

### Notes

<any additional context>
```

### Step 5: Update the Review Tracker

After creating issues, update `docs/code-review/README.md`:

1. Add a new section for this review batch with the date
2. List all newly created issues with their numbers and labels
3. Update the progress summary table

### Step 6: Report Summary

Print a summary for the user:

```
## Code Review Complete

**Scope:** <what was reviewed>
**Date:** <today>

### Findings
| Priority | Count | Issues |
|----------|-------|--------|
| 🔴 Critical | N | #X, #Y, ... |
| 🟡 Medium | N | #X, #Y, ... |
| 🟢 Low | N | #X, #Y, ... |
| 🔍 Needs Investigation | N | #X, #Y, ... |

**Total:** N new issues created
**Skipped:** N duplicates (already tracked)

View all: gh issue list --repo negativeeddy/arm-sharp --label agent-ready
```

## Labels

The following GitHub labels must exist (create if missing):

| Label | Color | Description |
|-------|-------|-------------|
| `code-review` | `0052CC` | Code review finding |
| `agent-ready` | `aaaaaa` | Ready for an agent to pick up and fix |
| `agent-claimed` | `E4A80B` | Currently being worked on by an agent |
| `priority: critical` | `D93F0B` | Must-fix: correctness or data-loss risk |
| `priority: medium` | `FBCA04` | Correctness or maintainability concern |
| `priority: low` | `0E8A16` | Polish, consistency, or minor performance |
| `needs-investigation` | `5319E7` | Requires deeper review before a fix can be prescribed |

Create missing labels:
```bash
gh api repos/negativeeddy/arm-sharp/labels -f name="code-review" -f color="0052CC" -f description="Code review finding" 2>/dev/null
gh api repos/negativeeddy/arm-sharp/labels -f name="agent-ready" -f color="aaaaaa" -f description="Ready for an agent to pick up and fix" 2>/dev/null
gh api repos/negativeeddy/arm-sharp/labels -f name="agent-claimed" -f color="E4A80B" -f description="Currently being worked on by an agent" 2>/dev/null
gh api repos/negativeeddy/arm-sharp/labels -f name="priority: critical" -f color="D93F0B" -f description="Must-fix: correctness or data-loss risk" 2>/dev/null
gh api repos/negativeeddy/arm-sharp/labels -f name="priority: medium" -f color="FBCA04" -f description="Correctness or maintainability concern" 2>/dev/null
gh api repos/negativeeddy/arm-sharp/labels -f name="priority: low" -f color="0E8A16" -f description="Polish, consistency, or minor performance" 2>/dev/null
gh api repos/negativeeddy/arm-sharp/labels -f name="needs-investigation" -f color="5319E7" -f description="Requires deeper review before a fix can be prescribed" 2>/dev/null
```

## References

- Review categories and severity definitions: `docs/code-review/README.md`
- Existing review findings: `docs/code-review/*.md`
- Project architecture: `ARCHITECTURE.md`, `docs/AGENTS.md`
