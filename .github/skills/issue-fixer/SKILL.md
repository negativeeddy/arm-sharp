---
name: issue-fixer
description: 'Work through open agent-ready issues from the GitHub issue board. Use when: fixing issues, working through cleanup tasks, resolving agent-ready labeled issues, running periodic fix sessions, addressing technical debt. Picks up any issue tagged agent-ready, not just code review findings.'
argument-hint: '[count|priority|issue#] — e.g. "3", "critical", "#82", "medium+low"'
---

# Issue Fix Worker

Picks up open `agent-ready` labeled issues from the GitHub issue board and implements fixes **one issue at a time — each on its own branch and its own pull request**. Designed to be run periodically (e.g., weekly) to work through accumulated ready-to-fix items.

The `agent-ready` label is the universal signal that an issue is available for automated fixing. Any process — the code review skill, manual issue creation, or other workflows — can add this label to indicate an issue is ready for an agent to pick up and complete.

**Label lifecycle:**
- `agent-ready` → issue is available for pickup
- `agent-claimed` → issue is actively being worked on (set when picking up, removed when done)
- Neither label → issue is completed or not part of the automated workflow

## When to Use

- Periodic cleanup sessions (weekly/biweekly)
- When the user says "fix some issues" or "work through the backlog"
- When the user specifies an issue number or priority to work on
- Sprint tech-debt reduction
- After the code review skill has created new findings

## Procedure

### Step 1: Determine What to Fix

Parse the argument hint to decide which issues to work on. Every issue selected gets its **own branch and its own PR** — never batch multiple issues into one branch/PR.

| Argument | Behavior |
|----------|----------|
| (none) | Pick the highest-priority open `agent-ready` issue |
| `N` (number) | Fix the next N issues in priority order, one branch/PR per issue |
| `critical` | Fix all `priority: critical` issues, one branch/PR per issue |
| `medium` | Fix all `priority: medium` issues, one branch/PR per issue |
| `low` | Fix all `priority: low` issues, one branch/PR per issue |
| `#NNN` | Fix the specific issue number (one branch/PR) |
| `investigation` | Work on `needs-investigation` issues (deep reviews) |
| `medium+low` | Fix all medium and low priority issues, one branch/PR per issue |

Fetch open issues:

```bash
gh issue list --repo negativeeddy/arm-sharp \
  --label agent-ready --state open \
  --limit 200 --json number,title,labels
```

Sort by priority: critical → medium → low. Skip `needs-investigation` issues unless explicitly requested (they need review first, not a blind fix).

### Step 2: Pick Up the Issue

Before starting work, transition the labels from `agent-ready` to `agent-claimed`:

```bash
gh issue edit <number> --repo negativeeddy/arm-sharp --remove-label "agent-ready" --add-label "agent-claimed"
```

### Step 3: Create a Working Branch (one per issue)

Always start from an up-to-date `master`, and give the branch a **per-issue** name. Never reuse a branch for more than one issue.

```bash
git checkout master
# Ensure the branch contains the latest merged fixes so each PR is small and conflict-free.
git pull --ff-only
# One branch per issue, named after that issue:
git checkout -b fix/issue-#<issue-number>
```

If a branch for this issue already exists (e.g. from a previous interrupted run), rebase it onto the latest `master` before continuing:

```bash
git checkout fix/issue-#<issue-number>
git rebase master
```

### Step 4: For Each Issue

#### 4a. Read the Issue

```bash
gh issue view <number> --repo negativeeddy/arm-sharp --json title,body,labels
```

#### 4b. Understand the Problem

- Read the affected files mentioned in the issue
- Understand the current behavior
- Review the proposed fix

#### 4c. Implement the Fix

- Make the code changes following the proposed fix in the issue
- If the proposed fix has multiple options, implement the recommended one
- If the fix requires more investigation than expected, comment on the issue and skip — swap `agent-claimed` back to `agent-ready`:
  ```bash
  gh issue comment <number> --repo negativeeddy/arm-sharp \
    --body "Investigation reveals this needs more analysis: <explanation>. Re-adding agent-ready label for future pickup."
  gh issue edit <number> --repo negativeeddy/arm-sharp --remove-label "agent-claimed" --add-label "agent-ready"
  ```

#### 4d. Verify the Fix

- Run the build: `dotnet build ArmRipper.slnx -c Debug`
- Run relevant tests: `dotnet test` (scoped to affected project if possible)
- If no tests exist for the changed code, consider adding one

#### 4e. Commit and Reference

```bash
git add <files>
git commit -m "fix: <short description> (closes #<number>)"
```

**Important:** The `closes #<number>` in the commit message only auto-closes the issue when the commit lands on the default branch (via PR merge). It does NOT auto-close from a feature-branch push. The PR body `Fixes` keyword is the reliable mechanism — see Step 5.

### Step 5: Create a Pull Request (per issue)

Create **one PR per issue** — the branch contains only that issue's fix, so the PR is small, focused, and easy to review. Cross-link both ways: the PR references the issue (`Fixes #N` so it auto-closes on merge), and the issue is updated with a comment linking to the PR.

```bash
git push -u origin fix/issue-#<issue-number>

gh pr create --repo negativeeddy/arm-sharp \
  --title "fix: <short description> (closes #<number>)" \
  --body "## Automated Fix

Fixes #<number>

### Changes
<brief summary of the fix>

### Testing
- All existing tests pass
- <any new tests added>"
```

**CRITICAL — `Fixes` keyword placement:** GitHub only auto-closes issues when `Fixes #N` appears on its own line as a **complete keyword phrase**. Bad formats that silently fail:

| Format | Works? | Why |
|--------|--------|-----|
| `Fixes #67 and #69` | Only #67 | GitHub parses the first `#N` after `Fixes`, ignores the rest |
| `fixes #67, #69` | Only #67 | Comma-separated list not recognized |
| `closes #67 in PR body` | No | `closes` must be immediately followed by `#N` |
| `Fixes #67` (own line) | **Yes** | Correct format |
| `Fixes #67` then `Fixes #69` (separate lines) | **Both** | Each keyword closes its issue |

Always put `Fixes #<number>` on its **own line** in the PR body. For multiple issues, use separate `Fixes` lines.

#### 5a. Link the Issue to the PR

After the PR is created, comment on the issue with the PR link so anyone viewing the issue can find the fix:

```bash
# Capture the PR URL from the create output, or look it up by branch:
pr_url=$(gh pr view fix/issue-#<issue-number> --repo negativeeddy/arm-sharp --json url --jq .url)

gh issue comment <issue-number> --repo negativeeddy/arm-sharp \
  --body "Fix submitted in PR: $pr_url (auto-closes this issue on merge)."
```

Both directions are now linked:
- **PR → Issue:** `Fixes #<number>` on its own line in the PR body (the reliable auto-close mechanism).
- **Issue → PR:** the comment above, so the issue thread shows where the fix lives.

#### 5b. Verify Auto-Close After Merge

After a PR is merged, verify the issue actually closed:

```bash
# Check the issue state — should be CLOSED
gh issue view <issue-number> --repo negativeeddy/arm-sharp --json state --jq .state
```

If the issue is still OPEN after the PR merged, the `Fixes` keyword wasn't recognized. Close it manually:

```bash
gh issue close <issue-number> --repo negativeeddy/arm-sharp --reason completed
```

Then add a comment explaining:
```bash
gh issue comment <issue-number> --repo negativeeddy/arm-sharp \
  --body "Closed manually — PR #<pr-number> was merged with this fix but auto-close did not trigger."
```

### Step 6: Move to Next Issue

After the PR for the current issue is created (and the issue is linked), start the next issue from a fresh branch off the latest `master`:

```bash
# Sync local master with any merged PRs (including your own earlier ones)
git checkout master
git pull --ff-only

# Repeat Steps 4–5 for the next issue
git checkout -b fix/issue-#<next-issue-number>
```

### Step 7: Report Summary

Print a summary with one row per issue/PR:

```
## Issue Fix Session Complete

**Issues Fixed:** N
**Issues Skipped:** N (needs investigation)

### Fixed (one PR each, cross-linked)
| Issue | Title | Priority | Branch/PR |
|-------|-------|----------|-----------|
| #N | <title> | 🟡 Medium | fix/issue-#N → PR #NN |
| #M | <title> | 🟢 Low | fix/issue-#M → PR #MM |

Each issue above has a comment linking to its PR, and each PR links back via `Fixes #N`.

### Skipped
| Issue | Title | Reason |
|-------|-------|--------|
| #P | <title> | Needs deeper investigation |

### Orphaned (auto-close failed)
| Issue | Merged PR | Action Taken |
|-------|-----------|--------------|
| #Q | PR #RR | Closed manually after verifying fix |
```

## Handling `needs-investigation` Issues

These issues require a deep review before a fix can be prescribed. When working on them:

1. **Read the investigation tasks** listed in the issue
2. **Explore the code** using read-only subagents
3. **Determine** if the code is actually robust (close the issue) or has real bugs
4. If bugs found:
   - Create a **new GitHub issue** for each bug, using the same template:
     ```bash
     gh issue create --repo negativeeddy/arm-sharp \
       --title "<concise bug title>" \
       --body-file <temp-file> \
       --label "agent-ready" \
       --label "priority: <critical|medium|low>"
     ```
     Issue body template:
     ```markdown
     ## <Title>

     **Source:** Investigation of #<original-issue-number>
     **Date:** <today>
     **File(s):** `<affected files>`

     ### Problem

     <description of the bug>

     ### Proposed Fix

     <fix description or code sample>

     ### Notes

     <any additional context>
     ```
   - Comment on the original issue with links to the new issues
   - If the bug is simple enough to fix directly, also create a branch and PR as usual (one per bug)
5. If code is robust:
   - Comment with evidence of why it's safe
   - Close the issue: `gh issue close <number> --repo negativeeddy/arm-sharp`

## Closing Orphaned Issues (Merged PRs That Didn't Auto-Close)

Sometimes an issue remains OPEN even though a PR that fixes it has been merged. This happens when:
- The PR body used `Fixes #N` in a non-standard format (e.g., `Fixes #67 and #69` — only the first gets closed)
- The `Fixes` keyword was missing from the PR body and the commit message only had it on a feature branch
- The PR used `closes`/`resolves` in a sentence rather than as a standalone keyword phrase

**Procedure to find and close orphaned issues:**

```bash
# 1. List all merged PRs
gh pr list --repo negativeeddy/arm-sharp --state merged --limit 100 \
  --json number,title,body,headRefName

# 2. For each PR body, extract issue references
# Look for "Fixes #N", "Fixes #N, Fixes #M" patterns
# Check if those issues are still OPEN

# 3. For each orphaned issue, verify the fix is actually in the codebase
# Then close it:
gh issue close <number> --repo negativeeddy/arm-sharp --reason completed
gh issue comment <number> --repo negativeeddy/arm-sharp \
  --body "Closed manually — PR #<pr-number> merged with this fix but auto-close did not trigger. Root cause: <explanation>."
```

## Safety Rules

- **Never modify running services** — only build and test
- **One issue per branch and PR** — never mix fixes for different issues in a single branch/PR; keep each issue's fix isolated
- **Cross-link every issue and PR** — PR body uses `Fixes #N` on its own line, and a comment on the issue links the PR
- **`Fixes #N` must be on its own line** — GitHub ignores `Fixes #N` when embedded in prose like `Fixes #N and #M`
- **One fix per commit** — easy to revert individual fixes
- **Always run tests** before committing
- **Skip if uncertain** — add `needs-investigation` label, swap `agent-claimed` back to `agent-ready`, and move on
- **Respect the priority order** — critical first, then medium, then low
- **Always branch from an up-to-date `master`** — pull before creating each issue branch so every PR is small and conflict-free
- **Verify auto-close after merge** — check that each issue actually closed; if not, close it manually with a comment
- **Label lifecycle** — `agent-ready` → `agent-claimed` (on pickup) → removed (on completion or skip)

## Quick Reference Commands

```bash
# List open agent-ready issues
gh issue list --repo negativeeddy/arm-sharp --label agent-ready --state open

# List issues currently being worked on
gh issue list --repo negativeeddy/arm-sharp --label agent-claimed --state open

# View a specific issue
gh issue view <number> --repo negativeeddy/arm-sharp

# Comment on an issue
gh issue comment <number> --repo negativeeddy/arm-sharp --body "..."

# Close an issue
gh issue close <number> --repo negativeeddy/arm-sharp

# Check if an issue was auto-closed after PR merge
gh issue view <number> --repo negativeeddy/arm-sharp --json state --jq .state

# Per-issue branch + PR workflow (repeat for each issue)
git checkout master && git pull --ff-only
git checkout -b fix/issue-#<issue-number>
# ... implement + verify + commit ...
git push -u origin fix/issue-#<issue-number>
gh pr create --repo negativeeddy/arm-sharp \
  --title "fix: <short description> (closes #<number>)" \
  --body "Automated fix.

Fixes #<number>

### Changes
<brief summary>

### Testing
- All existing tests pass"
# Cross-link the issue back to the PR so each references the other:
pr_url=$(gh pr view fix/issue-#<issue-number> --repo negativeeddy/arm-sharp --json url --jq .url)
gh issue comment <issue-number> --repo negativeeddy/arm-sharp --body "Fix submitted in PR: $pr_url (auto-closes this issue on merge)."

# Find orphaned issues (open issues with merged PRs that should have closed them)
gh pr list --repo negativeeddy/arm-sharp --state merged --limit 100 \
  --json number,title,body --jq '.[] | select(.body | test("Fixes #[0-9]+")) | "\(.number)\t\(.title)"'
```
